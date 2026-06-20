using ImpossibleRobert.Common;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using Brain;

namespace AssetInventory
{
    public sealed class CaptionCreator : AssetImporter
    {
        private static readonly Regex PreviewFilePattern = new Regex(@"^af(?:a)?-(\d+)\.png$", RegexOptions.Compiled);

        private static void PopulateVariables(Dictionary<string, string> variables, AssetFile af)
        {
            // Add common AssetFile properties as variables
            variables.Add("filename", af.FileName ?? string.Empty);
            variables.Add("path", af.Path ?? string.Empty);
            variables.Add("sourcepath", af.SourcePath ?? string.Empty);
            variables.Add("type", af.Type ?? string.Empty);
            variables.Add("size", af.Size.ToString());
            variables.Add("width", af.Width.ToString());
            variables.Add("height", af.Height.ToString());
            variables.Add("length", af.Length.ToString());
            variables.Add("assetid", af.AssetId.ToString());
            variables.Add("guid", af.Guid ?? string.Empty);
        }

        private static string PreparePrompt(string filePath)
        {
            string basePrompt = string.IsNullOrWhiteSpace(AI.Config.aiCustomPrompt)
                ? Intelligence.DefaultPrompt
                : AI.Config.aiCustomPrompt;

            // Extract AssetFile ID from filename and lookup in database
            // Filename format: af{aniSign}-{Id}.png (e.g., "af-12345.png" or "afa-12345.png")
            string fileName = Path.GetFileName(filePath);
            int assetFileId = 0;

            // Extract ID from filename using regex (af-{id}.png or afa-{id}.png)
            Match match = PreviewFilePattern.Match(fileName);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int parsedId))
            {
                assetFileId = parsedId;
            }

            // Create variables dictionary (always defined, even if empty)
            Dictionary<string, string> variables = new Dictionary<string, string>();

            // Lookup AssetFile in database and populate variables
            AssetFile assetInfo = (assetFileId > 0 ? DBAdapter.DB.Find<AssetFile>(assetFileId) : null) ?? new AssetFile();
            PopulateVariables(variables, assetInfo);

            string prompt = VariableResolver.ReplaceVariables(basePrompt, variables);
            return prompt;
        }

        public async Task Run()
        {
            List<string> types = new List<string>();
            if (AI.Config.aiForPrefabs) types.AddRange(AI.TypeGroups[AI.AssetGroup.Prefabs]);
            if (AI.Config.aiForImages) types.AddRange(AI.TypeGroups[AI.AssetGroup.Images]);
            if (AI.Config.aiForModels) types.AddRange(AI.TypeGroups[AI.AssetGroup.Models]);

            string typeStr = string.Join("\",\"", types);
            string query = "select *, AssetFile.Id as Id from AssetFile inner join Asset on Asset.Id = AssetFile.AssetId where Asset.Exclude = 0 and Asset.UseAI = 1 and AssetFile.Type in (\"" + typeStr + "\") and AssetFile.AICaption is null and (AssetFile.PreviewState = ? or AssetFile.PreviewState = ? or AssetFile.PreviewState = ?) order by Asset.Id desc";
            List<AssetInfo> files = DBAdapter.DB.Query<AssetInfo>(query, AssetFile.PreviewOptions.Custom, AssetFile.PreviewOptions.Provided, AssetFile.PreviewOptions.UseOriginal).ToList();

            await Run(files);
        }

        public async Task Run(List<AssetInfo> files)
        {
            if (files.Count == 0) return;

            if (AI.Config.aiBackend == 2)
            {
                if (!Intelligence.IsLMStudioInstalled)
                {
                    Debug.LogError("LM Studio server is not running. Start LM Studio and enable the local server first.");
                    return;
                }
            }

            string previewFolder = Paths.GetPreviewFolder();

            int chunkSize = 1;

            // Blip, Ollama and LMStudio all support batching
            if (AI.Config.aiBackend == 0) chunkSize = AI.Config.blipChunkSize;
            if (AI.Config.aiBackend == 1) chunkSize = AI.Config.ollamaParallelRequests;
            if (AI.Config.aiBackend == 2) chunkSize = AI.Config.lmStudioParallelRequests;

            bool toolChainWorking = true;

            MainCount = files.Count;
            for (int i = 0; i < files.Count; i += chunkSize)
            {
                if (CancellationRequested) break;
                await Task.Delay(Mathf.RoundToInt(AI.Config.aiPause * 1000f)); // to prevent system crashes or overheating

                List<AssetInfo> fileChunk = files.Skip(i).Take(chunkSize).ToList();
                List<string> previewFiles = new List<string>();

                foreach (AssetInfo file in fileChunk)
                {
                    SetProgress(file.FileName, i + 1);

                    string previewFile = ValidatePreviewFile(file, previewFolder);
                    if (!string.IsNullOrEmpty(previewFile))
                    {
                        previewFiles.Add(previewFile);
                    }
                }
                if (previewFiles.Count == 0) continue;

                await Task.Run(async () =>
                {
                    List<CaptionResult> captions = await CaptionImage(previewFiles, null, AI.Actions.CancellationToken);
                    if (captions != null && captions.Count > 0)
                    {
                        for (int j = 0; j < captions.Count; j++)
                        {
                            if (captions[j].caption != null)
                            {
                                fileChunk[j].AICaption = captions[j].caption.Truncate(AI.Config.aiMaxCaptionLength);
                                DBAdapter.DB.Execute("update AssetFile set AICaption=? where Id=?", fileChunk[j].AICaption, fileChunk[j].Id);

                                if (AI.Config.logAICaptions)
                                {
                                    Debug.Log($"Caption: {captions[j].caption} ({fileChunk[j].FileName})");
                                }
                            }
                            else if (i == 0)
                            {
                                if (!AI.Config.aiContinueOnEmpty) toolChainWorking = false;
                            }
                        }
                    }
                    else if (i == 0)
                    {
                        if (AI.Config.aiBackend == 0 && !AI.Config.aiContinueOnEmpty) toolChainWorking = false;
                    }
                });
                if (!toolChainWorking) break;
            }
        }

        /// <summary>
        /// Generates captions for a list of image files using the configured AI backend.
        /// </summary>
        public static async Task<List<CaptionResult>> CaptionImage(List<string> filenames, string modelName = null, System.Threading.CancellationToken cancellationToken = default)
        {
            // Prepare prompts for each file
            List<string> prompts = filenames.Select(PreparePrompt).ToList();

            // Use the Brain package's CaptionEngine
            List<CaptionResult> brainResults = await CaptionEngine.CaptionImages(filenames, prompts, modelName, null, cancellationToken);

            // Convert to Asset Inventory's CaptionResult format
            return brainResults?.Select(r => new CaptionResult {path = r.path, caption = r.caption}).ToList();
        }
    }
}
