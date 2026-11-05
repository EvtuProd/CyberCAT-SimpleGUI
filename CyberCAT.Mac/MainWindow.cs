using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.ComponentModel;
using CyberCAT.Core;
using WolvenKit.RED4.Save;
using WolvenKit.RED4.Save.Classes;
using WolvenKit.RED4.Types;
using static WolvenKit.RED4.Types.Enums;
using Window = Avalonia.Controls.Window;

namespace CyberCAT.Mac;

public sealed class MainWindow : Window
{
    private readonly Button _open = new() { Content = "Open save…" };
    private readonly Button _save = new() { Content = "Save changes", IsEnabled = false };
    private readonly Button _saveCopy = new() { Content = "Save as…", IsEnabled = false };
    private readonly TextBlock _file = new() { Text = "No save loaded", TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _path = new() { FontSize = 11, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = new() { Text = "Loading game data…", TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 3 };
    private readonly ContentControl _body = new();
    private SaveDocument? _document;
    private bool _busy = true;
    private bool _allowClose;
    private bool _closingPrompt;

    public MainWindow()
    {
        Title = "CyberCAT — Cyberpunk 2077 Save Editor";
        Width = 1100;
        Height = 760;
        MinWidth = 820;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(24) };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 20) };
        var title = new StackPanel { Spacing = 4 };
        title.Children.Add(new TextBlock { Text = "CyberCAT", FontSize = 26, FontWeight = FontWeight.SemiBold });
        title.Children.Add(_file);
        title.Children.Add(_path);
        header.Children.Add(title);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(_open);
        actions.Children.Add(_saveCopy);
        actions.Children.Add(_save);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        layout.Children.Add(header);
        Grid.SetRow(_body, 1);
        layout.Children.Add(_body);
        Grid.SetRow(_progress, 2);
        layout.Children.Add(_progress);
        _status.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(_status, 3);
        layout.Children.Add(_status);
        Content = layout;
        _body.Content = Welcome();
        _open.IsEnabled = false;
        _open.Click += async (_, _) => await OpenSave();
        _save.Click += async (_, _) => await Save(false);
        _saveCopy.Click += async (_, _) => await Save(true);
        Opened += async (_, _) => await Initialize();
        Closing += async (_, e) =>
        {
            if (_allowClose) return;
            if (_busy) { e.Cancel = true; return; }
            if (_document?.IsDirty != true) return;
            e.Cancel = true;
            if (_closingPrompt) return;
            _closingPrompt = true;
            try
            {
                if (await Confirm("Discard changes?", "This save has unsaved changes. Close without saving?", "Discard changes"))
                {
                    _allowClose = true;
                    Close();
                }
            }
            finally { _closingPrompt = false; }
        };
        KeyDown += async (_, e) =>
        {
            var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            if (_busy || !e.KeyModifiers.HasFlag(modifier)) return;
            if (e.Key == Key.O) { e.Handled = true; await OpenSave(); }
            if (e.Key == Key.S) { e.Handled = true; await Save(e.KeyModifiers.HasFlag(KeyModifiers.Shift)); }
        };
    }

    private static Control Welcome()
    {
        var panel = new StackPanel { Spacing = 16, MaxWidth = 540, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(Heading("Make Night City your own"));
        panel.Children.Add(Description("Open sav.dat from a Cyberpunk 2077 save folder to edit your character, inventory, quest facts, vehicles and appearance."));
        panel.Children.Add(Description("Your changes stay in memory until you save. Replacing a save creates a dated backup beside the original."));
        panel.Children.Add(Description("⌘O  Open save     ⌘S  Save changes     ⇧⌘S  Save a copy"));
        return panel;
    }

    private async Task Initialize()
    {
        try
        {
            await Task.Run(GameData.Initialize);
            Status("Ready. Open a save to begin.");
        }
        catch (Exception ex)
        {
            Status("Game data could not be loaded.");
            await Alert("Startup failed", ex.Message);
            return;
        }
        finally { SetBusy(false); }
        if (Program.SavePath is { } path) await LoadSave(path);
    }

    private async Task OpenSave()
    {
        if (_busy) return;
        if (_document?.IsDirty == true && !await Confirm("Discard changes?", "Open another save and discard unsaved changes?", "Discard changes")) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Cyberpunk 2077 save",
            AllowMultiple = false,
            SuggestedStartLocation = await SaveFolder(),
            FileTypeFilter = new[] { new FilePickerFileType("Cyberpunk 2077 save") { Patterns = new[] { "*.dat" } } }
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await LoadSave(path);
    }

    private async Task<IStorageFolder?> SaveFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            _document == null ? "" : Path.GetDirectoryName(_document.SourcePath)!,
            Path.Combine(home, "Library/Application Support/CD Projekt Red/Cyberpunk 2077/saves"),
            Path.Combine(home, "Library/Application Support/CD Projekt Red/Cyberpunk 2077"),
            Path.Combine(home, "Library/Containers/com.cdprojektred.Cyberpunk2077/Data/Library/Application Support/CD Projekt Red/Cyberpunk 2077"),
            Path.Combine(home, "Saved Games/CD Projekt Red/Cyberpunk 2077"),
            home
        };
        foreach (var path in candidates)
            if (Directory.Exists(path)) return await StorageProvider.TryGetFolderFromPathAsync(path);
        return null;
    }

    private async Task LoadSave(string path)
    {
        SetBusy(true);
        Status("Reading save…");
        try
        {
            var document = await Task.Run(() => SaveDocument.Load(path));
            _document = document;
            document.Changed += () => Dispatcher.UIThread.Post(UpdateTitle);
            UpdateTitle();
            BuildEditor();
            Status($"Loaded {Path.GetFileName(Path.GetDirectoryName(path))}. Game build: {document.File.FileHeader.GameVersion}.");
        }
        catch (Exception ex)
        {
            Status("The save could not be opened.");
            await Alert("Cannot open save", ex.Message);
        }
        finally { SetBusy(false); }
    }

    private async Task Save(bool copy)
    {
        if (_busy || _document == null) return;
        _save.Focus();
        var path = _document.SourcePath;
        if (copy)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a save slot folder for the copy",
                AllowMultiple = false,
                SuggestedStartLocation = await SaveFolder()
            });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } folder) return;
            path = Path.Combine(folder, "sav.dat");
            if (System.IO.File.Exists(path) && !await Confirm("Replace this save?", $"Replace {path}? A backup of the current file will be created.", "Replace save")) return;
        }
        SetBusy(true);
        Status("Writing and verifying save…");
        try
        {
            var backup = await Task.Run(() => _document.SaveTo(path));
            Status(backup == null ? $"Saved to {path}" : $"Saved. Backup: {Path.GetFileName(backup)}");
        }
        catch (Exception ex)
        {
            Status("Save failed. Your changes are still open.");
            await Alert("Cannot save", ex.Message);
        }
        finally { SetBusy(false); }
    }

    private void BuildEditor()
    {
        var tabs = new TabControl();
        var pages = new (string Name, Func<Control> Build)[]
        {
            ("Player", PlayerPage), ("Inventory", InventoryPage), ("Quest facts", FactsPage),
            ("Vehicles", VehiclesPage), ("Appearance", AppearancePage), ("Extras", ExtrasPage)
        };
        foreach (var (name, build) in pages)
        {
            Control page;
            try { page = build(); }
            catch (Exception ex) { page = Description($"This section is unavailable in this save: {ex.Message}"); }
            tabs.Items.Add(new TabItem { Header = name, Content = new Border { Padding = new Thickness(0, 20, 0, 0), Child = page } });
        }
        _body.Content = tabs;
    }

    private Control PlayerPage()
    {
        var doc = _document!;
        var player = doc.Player;
        if (player == null) return Description("Player development data was not found in this save.");
        var panel = new StackPanel { Spacing = 16, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(Heading("Player progression"));
        if (!doc.CanEditPlayerStats) panel.Children.Add(Description("The player stats map is missing. Attributes and levels cannot be edited in this save."));
        var attributes = new[] { gamedataStatType.Strength, gamedataStatType.Reflexes, gamedataStatType.TechnicalAbility, gamedataStatType.Intelligence, gamedataStatType.Cool };
        var names = new[] { "Body", "Reflexes", "Technical ability", "Intelligence", "Cool" };
        for (var i = 0; i < attributes.Length; i++)
        {
            var attribute = player.Attributes.FirstOrDefault(x => x.AttributeName == attributes[i]);
            if (attribute == null) continue;
            var type = attributes[i];
            panel.Children.Add(NumberRow(names[i], attribute.Value, 0, Math.Max(20, (int)attribute.Value), value =>
            {
                doc.SetStat(type, (int)value);
                attribute.Value = (int)value;
                doc.MarkChanged();
            }, doc.CanEditPlayerStats));
        }
        panel.Children.Add(Heading("Levels & skills"));
        var proficiencyNames = new Dictionary<gamedataProficiencyType, string>
        {
            [gamedataProficiencyType.Level] = "Level",
            [gamedataProficiencyType.StreetCred] = "Street cred",
            [gamedataProficiencyType.CoolSkill] = "Headhunter",
            [gamedataProficiencyType.IntelligenceSkill] = "Netrunner",
            [gamedataProficiencyType.ReflexesSkill] = "Shinobi",
            [gamedataProficiencyType.StrengthSkill] = "Solo",
            [gamedataProficiencyType.TechnicalAbilitySkill] = "Engineer"
        };
        foreach (var proficiency in player.Proficiencies)
        {
            if (!proficiencyNames.TryGetValue(proficiency.Type, out var label)) continue;
            if (!Enum.TryParse<gamedataStatType>(proficiency.Type.ToString(), out var stat)) continue;
            var type = stat;
            panel.Children.Add(NumberRow(label, proficiency.CurrentLevel, 0, Math.Max(60, (int)proficiency.CurrentLevel), value =>
            {
                doc.SetStat(type, (int)value);
                proficiency.CurrentLevel = (int)value;
                if (value == 60) proficiency.CurrentExp = 0;
                doc.MarkChanged();
            }, doc.CanEditPlayerStats));
        }
        panel.Children.Add(Heading("Unspent points"));
        foreach (var points in player.DevPoints)
        {
            var label = (gamedataDevelopmentPointType)points.Type switch
            {
                gamedataDevelopmentPointType.Attribute => "Attribute points",
                gamedataDevelopmentPointType.Primary => "Perk points",
                gamedataDevelopmentPointType.Espionage => "Relic points",
                _ => null
            };
            if (label != null) panel.Children.Add(NumberRow(label, points.Unspent, 0, int.MaxValue, value => { points.Unspent = (int)value; doc.MarkChanged(); }));
        }
        return new ScrollViewer { Content = panel };
    }

    private Control InventoryPage()
    {
        var doc = _document!;
        if (doc.Inventory == null) return Description("No inventory data was found.");
        var containers = doc.Inventory.SubInventories;
        var container = new ComboBox { ItemsSource = containers.Select(x => x.InventoryId == 1 ? "Player inventory" : $"Container {x.InventoryId}").ToArray(), SelectedIndex = containers.Count > 0 ? 0 : -1, MinWidth = 200 };
        var search = new TextBox { Watermark = "Search item ID or type…", MinWidth = 320 };
        var table = Table(("Item ID", "Name", "3*"), ("Type", "Type", "*"), ("Quantity", "Quantity", "100"));
        var selected = new TextBlock { Text = "Select an item to edit its quantity and flags.", TextWrapping = TextWrapping.Wrap };
        var quantity = Number(1, 0, uint.MaxValue);
        quantity.IsEnabled = false;
        var quest = new CheckBox { Content = "Quest item" };
        var locked = new CheckBox { Content = "Cannot unequip" };
        var updating = false;
        void Refresh()
        {
            if (container.SelectedIndex < 0) { table.ItemsSource = Array.Empty<InventoryRow>(); return; }
            var query = search.Text ?? "";
            table.ItemsSource = containers[container.SelectedIndex].Items.Select(item => new InventoryRow(item))
                .Where(x => (x.Name + " " + x.Type).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            quantity.IsEnabled = quest.IsEnabled = locked.IsEnabled = false;
        }
        container.SelectionChanged += (_, _) => Refresh();
        search.TextChanged += (_, _) => Refresh();
        table.SelectionChanged += (_, _) =>
        {
            if (table.SelectedItem is not InventoryRow row) { quantity.IsEnabled = quest.IsEnabled = locked.IsEnabled = false; return; }
            updating = true;
            selected.Text = row.Name;
            quantity.Value = row.Item.Quantity;
            quantity.IsEnabled = row.Item.HasQuantity();
            quest.IsChecked = row.Item.Flags.HasFlag(ItemFlag.IsQuestItem);
            locked.IsChecked = row.Item.Flags.HasFlag(ItemFlag.IsNotUnequippable);
            quest.IsEnabled = locked.IsEnabled = true;
            updating = false;
        };
        void ApplyItem()
        {
            if (updating || quantity.Value == null || table.SelectedItem is not InventoryRow row) return;
            var amount = RequireNumber(quantity);
            if (row.Item.HasQuantity()) row.Item.Quantity = (uint)amount;
            row.Item.Flags = (quest.IsChecked == true ? ItemFlag.IsQuestItem : 0) | (locked.IsChecked == true ? ItemFlag.IsNotUnequippable : 0);
            doc.MarkChanged();
            row.NotifyChanged();
            Status("Item updated. Save to write your changes.");
        }
        quantity.ValueChanged += async (_, _) => await Guard(ApplyItem);
        quest.IsCheckedChanged += async (_, _) => await Guard(ApplyItem);
        locked.IsCheckedChanged += async (_, _) => await Guard(ApplyItem);
        Refresh();
        return Listing(Horizontal(container, search), table, Vertical(selected, Horizontal(new TextBlock { Text = "Quantity", VerticalAlignment = VerticalAlignment.Center }, quantity, quest, locked), Description("Quantity and flag edits apply immediately. Save changes to write them to the displayed save slot.")));
    }

    private Control FactsPage()
    {
        var doc = _document!;
        var search = new TextBox { Watermark = "Search fact name or hash…", MinWidth = 350 };
        var table = Table(("Fact", "Name", "3*"), ("Hash", "Hash", "*"), ("Value", "Value", "100"));
        var name = new TextBox { Watermark = "Fact name or numeric hash", MinWidth = 350 };
        var value = Number(0, 0, uint.MaxValue);
        var apply = new Button { Content = "Set fact" };
        void Refresh()
        {
            var query = search.Text ?? "";
            table.ItemsSource = doc.Facts.Select(x => new FactRow(x)).Where(x => (x.Name + " " + x.Hash).Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Name).ToList();
        }
        search.TextChanged += (_, _) => Refresh();
        table.SelectionChanged += (_, _) =>
        {
            if (table.SelectedItem is not FactRow row) return;
            name.Text = row.Hash.ToString();
            value.Value = row.Value;
        };
        apply.Click += async (_, _) => await Guard(() =>
        {
            doc.SetFact(name.Text?.Trim() ?? "", (uint)RequireNumber(value));
            Refresh();
            Status("Quest fact updated. Save to write your changes.");
        });
        Refresh();
        return Listing(Vertical(Description("Quest facts control story and mission state. Select an existing fact or enter a fact name to add one."), search), table, Horizontal(name, value, apply));
    }

    private Control VehiclesPage()
    {
        var doc = _document!;
        var garage = doc.Garage;
        if (garage == null) return Description("No vehicle garage was found. Unlock a vehicle in-game and create a new save first.");
        var search = new TextBox { Watermark = "Search vehicle ID…", MinWidth = 350 };
        var table = Table(("Vehicle ID", "Name", "4*"), ("Unlocked", "Status", "*"));
        var toggle = new Button { Content = "Toggle selected vehicle", IsEnabled = false };
        void Refresh()
        {
            var unlocked = garage.UnlockedVehicleArray.Select(x => (ulong)x.VehicleID.RecordID).ToHashSet();
            var names = GameData.Vehicles.Concat(garage.UnlockedVehicleArray.Select(x => x.VehicleID.RecordID.ResolvedText).OfType<string>()).Distinct().Order().ToList();
            table.ItemsSource = names.Where(x => x.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase))
                .Select(x => new VehicleRow(x, unlocked.Contains(TweakDBID.CalculateHash(x)))).ToList();
            toggle.IsEnabled = false;
        }
        search.TextChanged += (_, _) => Refresh();
        table.SelectionChanged += (_, _) => toggle.IsEnabled = table.SelectedItem is VehicleRow;
        toggle.Click += async (_, _) => await Guard(() =>
        {
            if (table.SelectedItem is not VehicleRow row) return;
            var id = TweakDBID.CalculateHash(row.Name);
            var existing = garage.UnlockedVehicleArray.FirstOrDefault(x => (ulong)x.VehicleID.RecordID == id);
            if (existing != null) garage.UnlockedVehicleArray.Remove(existing);
            else garage.UnlockedVehicleArray.Add(new vehicleUnlockedVehicle { VehicleID = new vehicleGarageVehicleID { RecordID = row.Name } });
            doc.MarkChanged();
            Refresh();
            Status("Garage updated. Save to write your changes.");
        });
        Refresh();
        return Listing(search, table, toggle);
    }

    private Control AppearancePage()
    {
        var doc = _document!;
        if (doc.Appearance == null) return Description("No character appearance was found in this save.");
        var export = new Button { Content = "Export preset…" };
        var import = new Button { Content = "Import preset…" };
        var editor = new TextBox { AcceptsReturn = true, FontFamily = new FontFamily("Menlo,monospace"), Text = doc.ExportAppearance(), TextWrapping = TextWrapping.NoWrap };
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(editor, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        var apply = new Button { Content = "Apply appearance JSON" };
        export.Click += async (_, _) =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export appearance preset", SuggestedFileName = "V.v2preset", DefaultExtension = "v2preset",
                FileTypeChoices = new[] { new FilePickerFileType("CyberCAT appearance preset") { Patterns = new[] { "*.v2preset" } } }
            });
            if (file?.TryGetLocalPath() is { } path) await Guard(() => { System.IO.File.WriteAllText(path, doc.ExportAppearance()); Status("Appearance preset exported."); });
        };
        import.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import appearance preset", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("CyberCAT appearance preset") { Patterns = new[] { "*.v2preset", "*.json" } } }
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
                await Guard(() => { doc.ImportAppearance(System.IO.File.ReadAllText(path)); editor.Text = doc.ExportAppearance(); Status("Preset imported. Save to write your changes."); });
        };
        apply.Click += async (_, _) => await Guard(() => { doc.ImportAppearance(editor.Text ?? ""); editor.Text = doc.ExportAppearance(); Status("Appearance updated. Save to write your changes."); });
        return Listing(Vertical(Description("Import and export .v2preset files, or edit the preset below. Imported presets must use the same body type as the character. Apply JSON edits before saving."), Horizontal(export, import)), editor, apply);
    }

    private Control ExtrasPage()
    {
        var doc = _document!;
        var travel = new Button { Content = "Unlock all fast travel points", IsEnabled = doc.CanRewritePackage("ScriptableSystemsContainer") && doc.Scriptable?.Chunks.OfType<FastTravelSystem>().Any() == true };
        travel.Click += async (_, _) => await Guard(() =>
        {
            var system = doc.Scriptable!.Chunks.OfType<FastTravelSystem>().First();
            foreach (var record in GameData.FastTravel)
                if (!system.FastTravelNodes.Any(x => x.Chunk?.PointRecord == (TweakDBID)record.PointRecord))
                    system.FastTravelNodes.Add(new gameFastTravelPointData { MarkerRef = record.MarkerRef, PointRecord = record.PointRecord });
            doc.MarkChanged();
            Status("Fast travel points unlocked. Save to write your changes.");
        });
        return Vertical(Heading("Extra actions"), Description(travel.IsEnabled
            ? "These actions update the open save. Use Save changes to write the result."
            : "Fast travel editing is unavailable for this save because its script data contains unsupported fields."), travel);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _progress.IsVisible = busy;
        _open.IsEnabled = !busy;
        _save.IsEnabled = _saveCopy.IsEnabled = !busy && _document != null;
        _body.IsEnabled = !busy;
    }

    private void UpdateTitle()
    {
        var doc = _document!;
        var dirty = doc.IsDirty ? " • Unsaved changes" : "";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var gameRoot = Path.Combine(home, "Library/Application Support/CD Projekt Red/Cyberpunk 2077/saves") + Path.DirectorySeparatorChar;
        var slot = Path.GetFileName(Path.GetDirectoryName(doc.SourcePath));
        _file.Text = slot + (doc.SourcePath.StartsWith(gameRoot, StringComparison.Ordinal) ? " — Game save slot" : " — External copy") + dirty;
        _path.Text = doc.SourcePath;
        ToolTip.SetTip(_path, doc.SourcePath);
        Title = $"{Path.GetFileName(Path.GetDirectoryName(doc.SourcePath))}{(doc.IsDirty ? " *" : "")} — CyberCAT";
    }

    private void Status(string text) => _status.Text = text;

    private async Task Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { await Alert("Cannot apply change", ex.Message); }
    }

    private Task Alert(string title, string message) => Dialog(title, message, "OK", false);
    private Task<bool> Confirm(string title, string message, string accept) => Dialog(title, message, accept, true);

    private async Task<bool> Dialog(string title, string message, string accept, bool cancel)
    {
        var dialog = new Window { Title = title, Width = 500, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var ok = new Button { Content = accept, IsDefault = true };
        ok.Click += (_, _) => dialog.Close(true);
        var buttons = Horizontal();
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        if (cancel)
        {
            var no = new Button { Content = "Cancel", IsCancel = true };
            no.Click += (_, _) => dialog.Close(false);
            buttons.Children.Add(no);
        }
        buttons.Children.Add(ok);
        var content = Vertical(Heading(title), Description(message), buttons);
        content.Margin = new Thickness(24);
        dialog.Content = content;
        return await dialog.ShowDialog<bool>(this);
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 20, FontWeight = FontWeight.SemiBold };
    private static TextBlock Description(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private static StackPanel Horizontal(params Control[] controls)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }
    private static StackPanel Vertical(params Control[] controls)
    {
        var panel = new StackPanel { Spacing = 14 };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }
    private static Grid Listing(Control top, Control middle, Control bottom)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 14 };
        grid.Children.Add(top);
        Grid.SetRow(middle, 1);
        grid.Children.Add(middle);
        Grid.SetRow(bottom, 2);
        grid.Children.Add(bottom);
        return grid;
    }
    private static NumericUpDown Number(decimal value, decimal min, decimal max) => new()
    {
        Minimum = min, Maximum = max, Value = value, Increment = 1, FormatString = "0", Width = 135
    };
    private static decimal RequireNumber(NumericUpDown number) => number.Value is { } value && value >= number.Minimum && value <= number.Maximum && decimal.Truncate(value) == value
        ? value : throw new ArgumentException("Enter a whole number within the allowed range.");
    private Control NumberRow(string label, decimal value, decimal min, decimal max, Action<decimal> changed, bool enabled = true)
    {
        var input = Number(value, min, max);
        input.IsEnabled = enabled;
        var accepted = value;
        var reverting = false;
        input.ValueChanged += async (_, _) =>
        {
            if (reverting || input.Value == null) return;
            try
            {
                var next = RequireNumber(input);
                changed(next);
                accepted = next;
            }
            catch (Exception ex)
            {
                reverting = true;
                input.Value = accepted;
                reverting = false;
                await Alert("Cannot apply change", ex.Message);
            }
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("260,Auto") };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(input, 1);
        row.Children.Add(input);
        return row;
    }
    private static DataGrid Table(params (string Title, string Property, string Width)[] columns)
    {
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserResizeColumns = true, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, SelectionMode = DataGridSelectionMode.Single };
        foreach (var (title, property, width) in columns)
            grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property), Width = width.EndsWith('*')
                ? new DataGridLength(width.Length == 1 ? 1 : double.Parse(width[..^1], System.Globalization.CultureInfo.InvariantCulture), DataGridLengthUnitType.Star)
                : new DataGridLength(double.Parse(width, System.Globalization.CultureInfo.InvariantCulture)) });
        return grid;
    }
}

public sealed class InventoryRow(ItemData item) : INotifyPropertyChanged
{
    public ItemData Item { get; } = item;
    public string Name => Item.ItemInfo.ItemId.Id.ResolvedText ?? $"Unknown item ({(ulong)Item.ItemInfo.ItemId.Id})";
    public string Type => GameData.Items.TryGetValue(Item.ItemInfo.ItemId.Id, out var record) ? record.Type : "Unknown";
    public uint Quantity => Item.HasQuantity() ? Item.Quantity : 1;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void NotifyChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Quantity)));
}

public sealed class FactRow(FactsTable.FactEntry fact)
{
    public uint Hash => fact.FactName;
    public string Name => GameData.Facts.GetValueOrDefault(Hash, "Unknown fact");
    public uint Value => fact.Value;
}

public sealed record VehicleRow(string Name, bool Unlocked)
{
    public string Status => Unlocked ? "Yes" : "No";
}
