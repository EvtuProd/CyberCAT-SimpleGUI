using System.Security.Cryptography;
using CyberCAT.Core;
using WolvenKit.Common.FNV1A;
using WolvenKit.RED4.Save;
using WolvenKit.RED4.Save.Classes;
using WolvenKit.RED4.Types;
using System.Text.Json.Nodes;

static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}

GameData.Initialize();
Check(GameData.Facts.Count > 1000 && GameData.Items.Count > 1000, "Bundled game databases load on the current CPU");
Check(((TweakDBID)"Items.money").ResolvedText == "Items.money", "Native Kraken decompression and item name resolution work");

var directory = Path.Combine(Path.GetTempPath(), "CyberCAT-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
Console.WriteLine("Test output: " + directory);
var facts = new FactsTable();
facts.FactEntries.Add(new FactsTable.FactEntry { FactName = "cybercat_test", Value = 1 });
var tableNode = new NodeEntry { Name = "FactsTable", Value = facts };
var db = new FactsDB { FactsTables = new() { facts }, TrailingBytes = Array.Empty<byte>() };
var dbNode = new NodeEntry { Name = "FactsDB", Value = db };
dbNode.AddChild(tableNode);
var questNode = new NodeEntry { Name = "questSystem" };
questNode.AddChild(dbNode);
var inventory = new Inventory();
inventory.SubInventories.Add(new SubInventory
{
    InventoryId = 1,
    Items = new()
    {
        new ItemData
        {
            ItemInfo = new ItemInfo { ItemId = new gameItemID { Id = "Items.money", RngSeed = 2 }, ItemStructure = ItemStructure.Quantity },
            Quantity = 100
        }
    }
});
var fixture = new CyberpunkSaveFile
{
    FileHeader = new CyberpunkSaveHeaderStruct
    {
        GameVersion = (uint)Enums.gameGameVersion.Current,
        SaveVersion = 200,
        ArchiveVersion = 1,
        GameDefPath = "base\\gameplay\\game_definitions\\gamedefault.redgame"
    },
    CompressionSettings = new Compression.Settings { TableEntriesCount = 0x400, ChunkSize = 0x80000 },
    Nodes = new() { questNode, new NodeEntry { Name = "inventory", Value = inventory } }
};
var path = Path.Combine(directory, "synthetic", "sav.dat");
new SaveDocument(fixture, path).SaveTo(path);
var original = System.IO.File.ReadAllBytes(path);
var document = SaveDocument.Load(path);
document.SetFact("cybercat_test", 42);
document.SetFact("cybercat_new_fact", 7);
document.Inventory!.SubInventories[0].Items[0].Quantity = 123456;
document.MarkChanged();
Check(document.IsDirty, "Changes mark the document as unsaved");
var backup = document.SaveTo(path);
Check(backup != null && System.IO.File.ReadAllBytes(backup).SequenceEqual(original), "Overwriting creates a byte-identical backup");
var reread = SaveDocument.Load(path);
Check(reread.Facts.Single(x => (uint)x.FactName == FNV1A32HashAlgorithm.HashString("cybercat_test")).Value == 42, "Existing fact survives write and re-read");
Check(reread.Facts.Single(x => (uint)x.FactName == FNV1A32HashAlgorithm.HashString("cybercat_new_fact")).Value == 7, "New fact uses the game's FNV-1a hash");
Check(reread.Inventory!.SubInventories[0].Items[0].Quantity == 123456, "Inventory edits survive write and re-read");
Check(!document.IsDirty, "Successful save clears the unsaved flag");
var beforeFailedWrite = System.IO.File.ReadAllBytes(path);
var settings = document.File.CompressionSettings.TableEntriesCount;
document.File.CompressionSettings.TableEntriesCount = 0;
document.MarkChanged();
var writeRejected = false;
try { document.SaveTo(path); } catch { writeRejected = true; }
Check(writeRejected && System.IO.File.ReadAllBytes(path).SequenceEqual(beforeFailedWrite) && document.IsDirty,
    "Failed serialization preserves the original file and unsaved changes");
document.File.CompressionSettings.TableEntriesCount = settings;
var invalid = Path.Combine(directory, "invalid.dat");
System.IO.File.WriteAllText(invalid, "This is not a save file");
var rejected = false;
try { SaveDocument.Load(invalid); } catch { rejected = true; }
Check(rejected, "Invalid files are rejected");
var conflicted = SaveDocument.Load(path);
var externalChange = System.IO.File.ReadAllBytes(path).Concat(new byte[] { 123 }).ToArray();
System.IO.File.WriteAllBytes(path, externalChange);
var conflictRejected = false;
try { conflicted.SaveTo(path); } catch (IOException) { conflictRejected = true; }
Check(conflictRejected && System.IO.File.ReadAllBytes(path).SequenceEqual(externalChange), "External changes to a loaded save are protected from overwrite");

// An optional real save is read only. All writes go into the test directory.
if (args.FirstOrDefault() is { } realPath)
{
    var hashBefore = SHA256.HashData(System.IO.File.ReadAllBytes(realPath));
    var real = SaveDocument.Load(realPath);
    Check(real.Inventory != null && real.Player != null, "Real save inventory and player data can be read");
    var factCount = real.Facts.Count();
    var inventoryCount = real.Inventory!.SubInventories.Sum(x => x.Items.Count);
    var money = real.Inventory.SubInventories.First(x => x.InventoryId == 1).Items.First(x => x.ItemInfo.ItemId.Id == (TweakDBID)"Items.money");
    var editedMoney = money.Quantity + 123;
    money.Quantity = editedMoney;
    real.SetFact("cybercat_roundtrip_test", 123);
    var attribute = real.Player!.Attributes.First();
    var editedAttribute = (int)attribute.Value == 20 ? 19 : (int)attribute.Value + 1;
    var attributeType = (Enums.gamedataStatType)attribute.AttributeName;
    var optimizedCount = OptimizedStats.Entries(real.Stats)?.Count;
    real.SetStat(attributeType, editedAttribute);
    attribute.Value = editedAttribute;
    var output = Path.Combine(directory, "real-copy", "sav.dat");
    real.SaveTo(output);
    var copy = SaveDocument.Load(output);
    Check(copy.Facts.Count() == factCount + 1 && copy.Inventory!.SubInventories.Sum(x => x.Items.Count) == inventoryCount, "Real save round-trip preserves existing facts and inventory counts");
    Check(copy.Inventory!.SubInventories.First(x => x.InventoryId == 1).Items.First(x => x.ItemInfo.ItemId.Id == (TweakDBID)"Items.money").Quantity == editedMoney, "Real save money edit survives round-trip");
    Check((int)copy.Player!.Attributes.First(x => x.AttributeName == attributeType).Value == editedAttribute, "Real save player attribute edit survives round-trip");
    if (optimizedCount > 0)
    {
        Check(OptimizedStats.Entries(copy.Stats)?.Count == optimizedCount, "Optimized stats entries are preserved");
        Check(OptimizedStats.Get(copy.Stats!, attributeType) == editedAttribute, "Player development data and the optimized forced modifier agree");
    }
    Check(copy.Facts.Single(x => (uint)x.FactName == FNV1A32HashAlgorithm.HashString("cybercat_roundtrip_test")).Value == 123, "Real save quest fact edit survives round-trip");
    if (real.Appearance != null)
    {
        var preset = real.ExportAppearance();
        real.ImportAppearance(preset);
        Check(real.ExportAppearance() == preset, "Appearance preset export/import round-trip");
    }
    Check(SHA256.HashData(System.IO.File.ReadAllBytes(realPath)).SequenceEqual(hashBefore), "Original real save remains unchanged");
    Check(System.IO.File.Exists(Path.Combine(directory, "real-copy", "metadata.9.json")), "Save copy includes its game metadata");
    var metadata = JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(directory, "real-copy", "metadata.9.json")))!;
    Check(metadata["Data"]!["metadata"]!["name"]!.GetValue<string>() == "real-copy", "Exported metadata points to the new save slot");
    Console.WriteLine($"Real save build: {real.File.FileHeader.GameVersion}; facts: {factCount}; items: {inventoryCount}");
}
Console.WriteLine("All smoke tests passed.");
