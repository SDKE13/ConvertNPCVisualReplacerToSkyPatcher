using Mutagen.Bethesda;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Mutagen.Bethesda.Plugins;
using System.Text;
using System.IO;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Order;
using nifly;
using ConvertNPCVisualReplacerToSkyPatcher.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using Newtonsoft.Json;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace ConvertNPCVisualReplacerToSkyPatcher
{
    public struct VisualOverride
    {
        public uint orgFormId;
        public string orgEditorId;
        public string orgMeshPath;
        public string orgTexturePath;
        public INpcGetter orgNPC;
        public uint newFormId;
        public string newEditorId;
        public string newMeshPath;
        public string newTexturePath;
        public uint compactFormId;
        public string compactEditorId;
    }
    public class Program
    {
        private static StringBuilder errorOutput = new StringBuilder();
        public static async Task<int> Main(string[] args)
        {
            return await SynthesisPipeline.Instance
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch, new PatcherPreferences()
                {
                    NoPatch = true,
                    ExclusionMods = new List<ModKey>() { ModKey.FromFileName("Synthesis.esp") }
                })
                .SetTypicalOpen(GameRelease.SkyrimSE, "YourPatcher.esp")                
                .Run(args);
        }

        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {

            var serializerSettings = new JsonSerializerSettings()
            {
                NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
                Formatting = Formatting.Indented
            };

            List<ModsToConvertModel>? mods = null;
            string FilePath = state.RetrieveConfigFile("ModsToConvertList.json");

            using (System.IO.TextReader tr = System.IO.File.OpenText(FilePath))
            {
                using (Newtonsoft.Json.JsonTextReader jr = new JsonTextReader(tr))
                {
                    mods = JsonConvert.DeserializeObject<List<ModsToConvertModel>>(tr.ReadToEnd(), serializerSettings);
                }
            }

            if (mods is null || mods.Count <= 0 || !mods.Exists(q => q.Convert))
            {
                WriteErrorLine($"No mods to convert!!!");
                return;
            }

            foreach (var mod in mods.Where(q => q.Convert))
            {
                errorOutput.Clear();

                ConvertToSkyPatcher(state, mod.ModFilename, mod.ModToConvertPath);

                FlushErrorlog(System.IO.Path.Combine(mod.ModToConvertPath, mod.ModFilename));
            }
        }

        private static void ConvertToSkyPatcher(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, string ModToConvert, string ModToConvertPath)
        {
            string meshPath = System.IO.Path.Combine(ModToConvertPath, "meshes", "actors", "character", "FaceGenData", "FaceGeom");
            string texturePath = System.IO.Path.Combine(ModToConvertPath, "textures", "actors", "character", "FaceGenData", "FaceTint");
            string binaryPath = System.IO.Path.Combine(state.DataFolderPath, ModToConvert);
            List<VisualOverride> overrideList = new List<VisualOverride>();
            var cache = state.LoadOrder.ToImmutableLinkCache();
            uint startCompactId = 0x0800;
            StringBuilder skypatchOut = new StringBuilder();

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(meshPath, ModToConvert));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(texturePath, ModToConvert));

            var ModToConvertOverLay = SkyrimMod.CreateFromBinary(binaryPath, SkyrimRelease.SkyrimSE);
            
            WriteErrorLine(string.Empty);
            WriteErrorLine($"Converting to Skypatch: {ModToConvert}");
            WriteErrorLine($"Path: {ModToConvertPath}");
            WriteErrorLine(string.Empty);

            ModToConvertOverLay.Npcs.ForEach(npc =>
            {
                if (string.Compare(npc.FormKey.ModKey.FileName, ModToConvertOverLay.ModKey.FileName, true) != 0)
                {
                    overrideList.Add(new VisualOverride
                    {
                        orgFormId = npc.FormKey.ID,
                        orgEditorId = npc.EditorID?.ToString() ?? string.Empty,
                        orgMeshPath = System.IO.Path.Combine(meshPath, npc.FormKey.ModKey.FileName),
                        orgTexturePath = System.IO.Path.Combine(texturePath, npc.FormKey.ModKey.FileName),
                        orgNPC = npc,
                        newFormId = npc.FormKey.ID,
                        newEditorId = CreateCloneEditorId(npc.EditorID?.ToString() ?? string.Empty, npc.FormKey.ID),
                        newMeshPath = System.IO.Path.Combine(meshPath, ModToConvert),
                        newTexturePath = System.IO.Path.Combine(texturePath, ModToConvert),
                        //compactFormId = ModToConvertOverLay.GetNextFormKey().f,
                        compactEditorId = CreateCloneEditorId(npc.EditorID?.ToString() ?? string.Empty, startCompactId++),
                    });
                }
            });

            bool saveFile = false;
            overrideList.ForEach(o =>
            {
                string meshFilename = $"{o.orgFormId:x8}.nif";
                string textureFilename = $"{o.orgFormId:x8}.dds";

                if (FormKey.TryFactory($"{o.newFormId.ToString("x6")}:{ModToConvertOverLay.ModKey.FileName}", out FormKey newKey))
                {
                    //var newNPC = state.PatchMod.Npcs.DuplicateInAsNewRecord(o.orgNPC, o.newEditorId);
                    saveFile = true;
                    ModToConvertOverLay.Npcs.Remove(o.orgNPC.FormKey);
                    var newNPC = ModToConvertOverLay.Npcs.DuplicateInAsNewRecord(o.orgNPC, o.newEditorId);
                    newNPC.EditorID = o.newEditorId;
                    newNPC.MajorRecordFlagsRaw &= ~(int)SkyrimMajorRecord.SkyrimMajorRecordFlag.Compressed;
                    // newNPC.EditorID = o.newEditorId;

                    uint newFormId = newNPC.FormKey.ID;
                    bool skypatch = false;

                    WriteErrorLine($"{o.orgEditorId} : {o.orgNPC.FormKey.ToString()} --> {newNPC.FormKey.ToString()}");

                    string newMeshFilename = $"{newFormId:x8}.nif".ToUpper();
                    string newTextureFilename = $"{newFormId:x8}.dds".ToUpper();
                    string ogTintMask = $@"{o.orgNPC.FormKey.ModKey.FileName}\{textureFilename}";
                    string newTintMask = $@"{newNPC.FormKey.ModKey.FileName}\{newTextureFilename}";
                    string meshPath = System.IO.Path.Combine(o.orgMeshPath, meshFilename);
                    string newMeshFilePath = System.IO.Path.Combine(o.newMeshPath, newMeshFilename);
                    
                    if (System.IO.File.Exists(meshPath))
                    {
                        System.IO.File.Copy(meshPath, newMeshFilePath, true);
                        UpdateNifTintPath(newMeshFilePath, ogTintMask, newTintMask);

                        if (ModToConvert.Contains("Bijin", StringComparison.InvariantCultureIgnoreCase))
                        {
                            var race = o.orgNPC.Race.Resolve(cache);
                            CleanBijinTexture(newMeshFilePath, race?.Name?.ToString()?.Replace(" ", string.Empty) ?? string.Empty);
                            newNPC.WornArmor.Clear();
                        }

                        skypatch = true;
                    }
                    else
                    {
                        WriteErrorLine($"{o.orgEditorId} mesh not found : {meshFilename}");
                    }

                    if (System.IO.File.Exists(System.IO.Path.Combine(o.orgTexturePath, textureFilename)))
                    {
                        System.IO.File.Copy(System.IO.Path.Combine(o.orgTexturePath, textureFilename), System.IO.Path.Combine(o.newTexturePath, newTextureFilename), true);
                        skypatch = true;
                    }
                    else
                    {
                        WriteErrorLine($"{o.orgEditorId} texture not found : {textureFilename}");
                    }

                    if (skypatch)
                    {
                        skypatchOut.AppendLine($";Name:{o.orgNPC.Name} EditorID:{o.orgNPC.EditorID?.ToString() ?? string.Empty}");
                        skypatchOut.AppendLine($"filterByNPCs={o.orgNPC.FormKey.ModKey.FileName}|{o.orgNPC.FormKey.ID.ToString("x8")}:copyVisualStyle={newNPC.FormKey.ModKey.FileName}|{newNPC.FormKey.ID.ToString("x8")}:weight={newNPC.Weight}:height={newNPC.Height}");
                    }
                }
            });

            if (saveFile)
            {
                WriteError("Back up original file:");
                if (!BackUpOriginalMod(System.IO.Path.Combine(ModToConvertPath, ModToConvert)))
                {
                    WriteErrorLine("> Save error, can not continue.");
                    return;
                }
                WriteErrorLine("> Done");

                WriteError("Saving mod:");
                try
                {
                    ModToConvertOverLay.WriteToBinary(binaryPath,
                    new BinaryWriteParameters()
                    {
                        MastersListOrdering = new MastersListOrderingByLoadOrder(state.LoadOrder),
                        Parallel = new ParallelWriteParameters() { MaxDegreeOfParallelism = 2 },
                        FormIDCompaction = FormIDCompactionOption.Iterate,
                        FormIDUniqueness = FormIDUniquenessOption.Iterate                        
                    });
                    
                    WriteErrorLine("> Done");

                    WriteErrorLine(string.Empty);
                    WriteErrorLine(string.Empty);
                    WriteErrorLine(string.Empty);
                }
                catch (Exception ex)
                {
                    WriteErrorLine("> Save error, can not continue.");
                    WriteErrorLine(ex?.Source ?? string.Empty);
                    WriteErrorLine(ex?.Message ?? string.Empty);
                    WriteErrorLine(ex?.StackTrace ?? string.Empty);
                    return;
                }                
            }

            if (skypatchOut.Length > 0)
            {
                string modFolderName = System.IO.Path.GetFileNameWithoutExtension(ModToConvert);
                string skyPatchPath = System.IO.Path.Combine(ModToConvertPath,
                    "SKSE",
                    "Plugins",
                    "SkyPatcher",
                    "npc",
                    modFolderName,
                    "visual");

                string skypatcherFullpath = System.IO.Path.Combine(skyPatchPath, $"{ModToConvert}.ini");

                WriteError($"Writing Skypatcher file to : {skypatcherFullpath}");

                System.IO.Directory.CreateDirectory(skyPatchPath);
                System.IO.File.WriteAllText(skypatcherFullpath, skypatchOut.ToString());

                WriteErrorLine("> Done");
            }
        }

        private static string CreateCloneEditorId(string editorId, uint formId)
        {
            string formID = $"{formId:x8}".ToUpper();
            return string.Format($"{editorId}_{formID}_clone");
        }

        private static bool BackUpOriginalMod(string OriginalModPath)
        {
            string modFilename = System.IO.Path.GetFileName(OriginalModPath);
            string modPath = System.IO.Path.GetDirectoryName(OriginalModPath) ?? string.Empty;
            string dateSuffix = DateTime.Now.ToString("yyyyMMddhhmmss");
            string backSuffix = "_bak";
            string backUpFilename = $"{modFilename}{backSuffix}_{dateSuffix}";

            try
            {
                System.IO.File.Copy(OriginalModPath, System.IO.Path.Combine(modPath, backUpFilename), false);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            catch (Exception ex)
            {
                WriteErrorLine("EXCEPTION!!");
                WriteErrorLine(ex?.Source ?? string.Empty);
                WriteErrorLine(ex?.Message ?? string.Empty);
                WriteErrorLine(ex?.StackTrace ?? string.Empty);
                return false;
            }

            return true;
        }

        private static bool UpdateNifTintPath(string filePath, string ogTintMask, string newTintMask)
        {
            using (nifly.NifFile nf = new NifFile())
            {
                nf.Load(filePath);
                bool doSave = false;
                var triShapes = nf.GetShapes();

                foreach (var shape in triShapes)
                {
                    if (!shape.HasShaderProperty() || string.IsNullOrWhiteSpace(nf.GetTexturePathByIndex(shape, 6)))
                    {
                        continue;
                    }
                    string textPath = nf.GetTexturePathByIndex(shape, 6);

                    if (!textPath.Contains(ogTintMask, StringComparison.InvariantCultureIgnoreCase))
                    {
                        continue;
                    }

                    textPath = textPath.Replace(ogTintMask, newTintMask, StringComparison.InvariantCultureIgnoreCase);
                    nf.SetTextureSlot(shape, textPath, 6);
                    doSave = true;
                }

                if (doSave)
                {
                    nf.Save(filePath);                    
                    return true;
                }
            }

            return false;
        }
        private static void CleanBijinTexture(string filePath, string race)
        {
            Dictionary<string, string> bijinTexturesToSkyrim = new Dictionary<string, string>();
            bijinTexturesToSkyrim.Add(@"bijin npcs\femalehead.dds", @"Female\FemaleHead.dds");
            bijinTexturesToSkyrim.Add(@"bijin npcs\femalehead_msn.dds", @$"{race}Female\FemaleHead_msn.dds");
            bijinTexturesToSkyrim.Add(@"bijin npcs\femalehead_sk.dds", @"Female\FemaleHead_sk.dds");
            bijinTexturesToSkyrim.Add(@"bijin npcs\femalehead_s.dds", @"Female\FemaleHead_s.dds");
            bijinTexturesToSkyrim.Add(@"bijin npcs 2\femalehead.dds", @"Female\FemaleHead.dds");
            bijinTexturesToSkyrim.Add(@"bijin npcs 2\femalehead_msn.dds", @$"{race}Female\FemaleHead_msn.dds");
            bijinTexturesToSkyrim.Add(@"bijin npcs 2\femalehead_sk.dds", @"Female\FemaleHead_sk.dds");
            bijinTexturesToSkyrim.Add(@"bijin npcs 2\femalehead_s.dds", @"Female\FemaleHead_s.dds");
            bijinTexturesToSkyrim.Add(@"bijin npcs 3\femalehead.dds", @"Female\FemaleHead.dds");
            bijinTexturesToSkyrim.Add(@"bijin npcs 3\femalehead_msn.dds", @$"{race}Female\FemaleHead_msn.dds");
            bijinTexturesToSkyrim.Add(@"bijin npcs 3\femalehead_sk.dds", @"Female\FemaleHead_sk.dds");
            bijinTexturesToSkyrim.Add(@"bijin npcs 3\femalehead_s.dds", @"Female\FemaleHead_s.dds");

            bijinTexturesToSkyrim.Add(@"bijin wives 00\femalehead.dds", @"Female\FemaleHead.dds");
            bijinTexturesToSkyrim.Add(@"bijin wives 00\femalehead_msn.dds", @$"{race}Female\FemaleHead_msn.dds");
            bijinTexturesToSkyrim.Add(@"bijin wives 00\femalehead_sk.dds", @"Female\FemaleHead_sk.dds");
            bijinTexturesToSkyrim.Add(@"bijin wives 00\femalehead_s.dds", @"Female\FemaleHead_s.dds");
            bijinTexturesToSkyrim.Add(@"bijin wives 01\femalehead.dds", @"Female\FemaleHead.dds");
            bijinTexturesToSkyrim.Add(@"bijin wives 01\femalehead_msn.dds", @$"{race}Female\FemaleHead_msn.dds");
            bijinTexturesToSkyrim.Add(@"bijin wives 01\femalehead_sk.dds", @"Female\FemaleHead_sk.dds");
            bijinTexturesToSkyrim.Add(@"bijin wives 01\femalehead_s.dds", @"Female\FemaleHead_s.dds");


            bijinTexturesToSkyrim.Add(@"bijin warmaidens 00\femalehead.dds", @"Female\FemaleHead.dds");
            bijinTexturesToSkyrim.Add(@"bijin warmaidens 00\femalehead_msn.dds", @$"{race}Female\FemaleHead_msn.dds");
            bijinTexturesToSkyrim.Add(@"bijin warmaidens 00\femalehead_sk.dds", @"Female\FemaleHead_sk.dds");
            bijinTexturesToSkyrim.Add(@"bijin warmaidens 00\femalehead_s.dds", @"Female\FemaleHead_s.dds");
            bijinTexturesToSkyrim.Add(@"bijin warmaidens 01\femalehead.dds", @"Female\FemaleHead.dds");
            bijinTexturesToSkyrim.Add(@"bijin warmaidens 01\femalehead_msn.dds", @$"{race}Female\FemaleHead_msn.dds");
            bijinTexturesToSkyrim.Add(@"bijin warmaidens 01\femalehead_sk.dds", @"Female\FemaleHead_sk.dds");
            bijinTexturesToSkyrim.Add(@"bijin warmaidens 01\femalehead_s.dds", @"Female\FemaleHead_s.dds");
            bijinTexturesToSkyrim.Add(@"bijin warmaidens 02\femalehead.dds", @"Female\FemaleHead.dds");
            bijinTexturesToSkyrim.Add(@"bijin warmaidens 02\femalehead_msn.dds", @$"{race}Female\FemaleHead_msn.dds");
            bijinTexturesToSkyrim.Add(@"bijin warmaidens 02\femalehead_sk.dds", @"Female\FemaleHead_sk.dds");
            bijinTexturesToSkyrim.Add(@"bijin warmaidens 02\femalehead_s.dds", @"Female\FemaleHead_s.dds");

            using (nifly.NifFile nf = new NifFile())
            {
                nf.Load(filePath);
                bool doSave = false;
                var triShapes = nf.GetShapes();

                foreach (var shape in triShapes)
                {
                    if (!shape.HasShaderProperty())
                    {
                        continue;
                    }

                    for (uint i = 0; i <= 8; i++)
                    {
                        string textPath = nf.GetTexturePathByIndex(shape, i);

                        foreach (var text in bijinTexturesToSkyrim)
                        {
                            if (textPath.Contains(text.Key, StringComparison.InvariantCultureIgnoreCase))
                            {
                                textPath = textPath.Replace(text.Key, text.Value, StringComparison.InvariantCultureIgnoreCase);
                                nf.SetTextureSlot(shape, textPath, i);                                
                                doSave = true;
                                break;
                            }
                        }
                    }
                }
                if (doSave)
                {
                    nf.Save(filePath);                    
                }
            }
        }
        private static void WriteErrorLine(string error)
        {
            System.Console.WriteLine(error);
            errorOutput.AppendLine(error);
        }

        private static void WriteError(string error)
        {
            System.Console.Write(error);
            errorOutput.Append(error);
        }

        private static bool FlushErrorlog(string OriginalModPath)
        {
            string modFilename = System.IO.Path.GetFileName(OriginalModPath);
            string modPath = System.IO.Path.GetDirectoryName(OriginalModPath) ?? string.Empty;
            string dateSuffix = DateTime.Now.ToString("yyyyMMddhhmmss");
            string errSuffix = ".err";
            string errorLogFilename = $"{modFilename}_{dateSuffix}{errSuffix}";

            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(modPath, errorLogFilename), errorOutput.ToString());                
            }
            catch (Exception ex)
            {
                WriteErrorLine("EXCEPTION!!");
                WriteErrorLine(ex?.Source ?? string.Empty);
                WriteErrorLine(ex?.Message ?? string.Empty);
                WriteErrorLine(ex?.StackTrace ?? string.Empty);
                return false;
            }

            return true;
        }
    }    
}
