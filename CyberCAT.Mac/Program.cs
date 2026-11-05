using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using CyberCAT.Core;
using System.Runtime.InteropServices;

namespace CyberCAT.Mac;

internal static class Program
{
    internal static string? SavePath;

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--check-save")
        {
            var document = SaveDocument.Load(args[1]);
            Console.WriteLine($"OK ({RuntimeInformation.ProcessArchitecture}): build {document.File.FileHeader.GameVersion}, {document.Facts.Count()} facts, {document.Inventory?.SubInventories.Sum(x => x.Items.Count)} items");
            return;
        }
        SavePath = args.FirstOrDefault(x => !x.StartsWith('-'));
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}

public sealed class App : Application
{
    public override void Initialize()
    {
        Name = "CyberCAT";
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://CyberCAT/"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml")
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
