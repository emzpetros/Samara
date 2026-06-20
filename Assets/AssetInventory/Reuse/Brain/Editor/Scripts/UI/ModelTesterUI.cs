using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImpossibleRobert.Common;
using UnityEditor;
using UnityEngine;

namespace Brain
{
    /// <summary>
    /// Editor window for testing and comparing AI models.
    /// </summary>
    public sealed class ModelTesterUI : CommonEditorUI
    {
        private const float MODEL_COLUMN_WIDTH = 150f;
        private const float IMAGE_COLUMN_WIDTH = 200f;
        private const float ROW_HEIGHT = 120f;
        private const float DISABLED_ROW_HEIGHT = 40f;
        private const float PREVIEW_SIZE = 80f;
        private const string BLIP_BACKEND = "[BLIP]";

        private List<TestModel> _models;
        private List<TestImage> _testImages;
        private Dictionary<string, bool> _modelEnabled;
        private Dictionary<string, bool> _imageEnabled;
        private Dictionary<(string model, string image), string> _results;
        private Dictionary<(string model, string image), float> _cellTimes;
        private Dictionary<string, float> _modelTotalTimes;
        private Vector2 _scroll;
        private bool _showPrompt;
        private bool _isRunning;
        private CancellationTokenSource _cts;
        private string _customPrompt;
        private string _imagePath;
        private Action<string> _onCustomPromptChanged;
        private bool _customPromptSeeded;

        public static ModelTesterUI ShowWindow(string imagePath, string initialCustomPrompt = null, Action<string> onCustomPromptChanged = null)
        {
            ModelTesterUI ui = GetWindow<ModelTesterUI>("AI Model Tester");
            ui.minSize = new Vector2(800, 600);
            ui._imagePath = imagePath;
            ui._onCustomPromptChanged = onCustomPromptChanged;
            ui._customPrompt = string.IsNullOrEmpty(initialCustomPrompt) ? null : initialCustomPrompt;
            ui._customPromptSeeded = true;
            ui.Init();
            return ui;
        }

        private void Init()
        {
            _testImages = GetTestImages() ?? new List<TestImage>();
            _models = new List<TestModel>();

            int currentBackend = Intelligence.Settings.AIBackend;
            if (currentBackend == 1) // Ollama
            {
                if (Intelligence.OllamaModels != null)
                {
                    _models.AddRange(Intelligence.OllamaModels
                        .OrderBy(m => m.Name, StringComparer.InvariantCultureIgnoreCase)
                        .Select(m => new TestModel {Name = m.Name, Backend = 1}));
                }
            }
            else if (currentBackend == 2) // LM Studio
            {
                if (Intelligence.LMStudioModels != null)
                {
                    _models.AddRange(Intelligence.LMStudioModels
                        .Where(m => m.type == "vlm" || (m.type != null && m.type.Contains("vision", StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(m => m.id, StringComparer.InvariantCultureIgnoreCase)
                        .Select(m => new TestModel {Name = m.id, Backend = 2}));
                }
            }

            if (_models.Any()) _models.Add(new TestModel {Name = BLIP_BACKEND, Backend = 0});

            _modelEnabled = _models.ToDictionary(m => m.Name, m => m.Name != BLIP_BACKEND);
            _imageEnabled = _testImages.ToDictionary(t => t.path, _ => true);
            _results = new Dictionary<(string, string), string>();
            _cellTimes = new Dictionary<(string, string), float>();
            _modelTotalTimes = new Dictionary<string, float>();
            _scroll = Vector2.zero;
            _isRunning = false;
            _cts = null;
            if (!_customPromptSeeded) _customPrompt = null;
        }

        private void OnEnable() => Init();

        private void OnGUI()
        {
            if (_models == null || _testImages == null || !_models.Any() || !_testImages.Any())
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("Data not yet ready. Make sure Ollama or LM Studio is running.", MessageType.Info);
                if (GUILayout.Button("Reload Data", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT))) Init();
                return;
            }

            float contentWidth = MODEL_COLUMN_WIDTH + _testImages.Count * IMAGE_COLUMN_WIDTH;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            // Header row
            EditorGUILayout.BeginHorizontal(GUILayout.Width(contentWidth), GUILayout.Height(ROW_HEIGHT));
            GUILayout.Space(MODEL_COLUMN_WIDTH);
            foreach (TestImage img in _testImages)
            {
                GUILayout.Space(6);
                EditorGUILayout.BeginVertical(GUILayout.Width(IMAGE_COLUMN_WIDTH), GUILayout.Height(ROW_HEIGHT));
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                _imageEnabled[img.path] = EditorGUILayout.Toggle(_imageEnabled[img.path], GUILayout.Width(20));
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                if (img.texture != null)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    GUILayout.Box(img.texture, CommonUIStyles.centerLabel, GUILayout.Width(PREVIEW_SIZE), GUILayout.Height(PREVIEW_SIZE));
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();

            // Data rows
            GUIStyle cellStyle = new GUIStyle(GUI.skin.textArea) {wordWrap = true};
            GUIStyle nameStyle = new GUIStyle(GUI.skin.label) {wordWrap = true};

            foreach (TestModel model in _models)
            {
                float rowHeight = _modelEnabled[model.Name] ? ROW_HEIGHT : DISABLED_ROW_HEIGHT;
                EditorGUILayout.BeginHorizontal(GUILayout.Width(contentWidth), GUILayout.Height(rowHeight));

                // Model column
                EditorGUILayout.BeginVertical(GUILayout.Width(MODEL_COLUMN_WIDTH), GUILayout.Height(rowHeight));
                EditorGUILayout.BeginHorizontal();
                _modelEnabled[model.Name] = EditorGUILayout.Toggle(_modelEnabled[model.Name], GUILayout.Width(16));
                GUILayout.Label(model.Name, nameStyle, GUILayout.Width(MODEL_COLUMN_WIDTH - 25));
                EditorGUILayout.EndHorizontal();

                if (_modelTotalTimes.TryGetValue(model.Name, out float totalTime))
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(23);
                    EditorGUILayout.LabelField($"Total: {totalTime:F2}s", EditorStyles.miniLabel, GUILayout.Width(MODEL_COLUMN_WIDTH - 25));
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndVertical();

                // Caption cells
                foreach (TestImage img in _testImages)
                {
                    EditorGUILayout.BeginVertical(GUILayout.Width(IMAGE_COLUMN_WIDTH), GUILayout.Height(rowHeight));
                    if (!_imageEnabled[img.path] || !_modelEnabled[model.Name])
                    {
                        EditorGUILayout.LabelField("-", EditorStyles.centeredGreyMiniLabel, GUILayout.Width(IMAGE_COLUMN_WIDTH), GUILayout.ExpandHeight(true));
                    }
                    else
                    {
                        (string m, string i) key = (model.Name, img.path);
                        _results.TryGetValue(key, out string caption);
                        EditorGUI.BeginDisabledGroup(true);
                        EditorGUILayout.TextArea(caption ?? string.Empty, cellStyle, GUILayout.Width(IMAGE_COLUMN_WIDTH), GUILayout.Height(rowHeight - 30));
                        EditorGUI.EndDisabledGroup();

                        EditorGUILayout.BeginHorizontal();
                        EditorGUI.BeginDisabledGroup(_isRunning);
                        if (GUILayout.Button("Run", EditorStyles.miniButton, GUILayout.Width(50)))
                        {
                            _ = RunSingleCaptionTestAsync(model, img.path, true);
                        }
                        EditorGUI.EndDisabledGroup();
                        if (_cellTimes.TryGetValue(key, out float cellTime))
                        {
                            EditorGUILayout.LabelField($"{cellTime:F2}s", EditorStyles.miniLabel, GUILayout.Width(50));
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUILayout.EndVertical();
                }

                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.Space();

            GUILayout.FlexibleSpace();
            _showPrompt = EditorGUILayout.BeginFoldoutHeaderGroup(_showPrompt, "Prompt");
            if (_showPrompt)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.BeginVertical();
                EditorGUILayout.LabelField("Default", EditorStyles.centeredGreyMiniLabel);
                EditorGUILayout.TextArea(Intelligence.DefaultPrompt);
                EditorGUILayout.EndVertical();
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.BeginVertical(GUILayout.Width(40), GUILayout.MaxHeight(150));
                GUILayout.FlexibleSpace();
                EditorGUI.BeginChangeCheck();
                if (_customPrompt == null)
                {
                    if (GUILayout.Button("Customize", GUILayout.ExpandWidth(false)))
                    {
                        _customPrompt = Intelligence.DefaultPrompt;
                    }
                }
                else
                {
                    if (GUILayout.Button("Use Default", GUILayout.ExpandWidth(false)))
                    {
                        _customPrompt = null;
                    }
                }
                bool toggled = EditorGUI.EndChangeCheck();
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndVertical();

                bool edited = false;
                if (_customPrompt != null)
                {
                    EditorGUILayout.BeginVertical();
                    EditorGUILayout.LabelField("Custom", EditorStyles.centeredGreyMiniLabel);
                    EditorGUI.BeginChangeCheck();
                    _customPrompt = EditorGUILayout.TextArea(_customPrompt);
                    edited = EditorGUI.EndChangeCheck();
                    EditorGUILayout.EndVertical();
                }
                EditorGUILayout.EndHorizontal();

                if (toggled || edited) _onCustomPromptChanged?.Invoke(_customPrompt);
            }
            EditorGUILayout.Space();
            EditorGUILayout.EndFoldoutHeaderGroup();

            // Controls
            EditorGUILayout.BeginHorizontal();
            if (!_isRunning)
            {
                if (GUILayout.Button("Create All Test Captions", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
                {
                    _cts = new CancellationTokenSource();
                    _ = RunCaptionTestsAsync(_cts.Token);
                }
            }
            else
            {
                if (GUILayout.Button("Cancel", GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
                {
                    _cts?.Cancel();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private async Task RunCaptionTestsAsync(CancellationToken token)
        {
            _isRunning = true;
            try
            {
                foreach (TestModel model in _models.Where(m => _modelEnabled[m.Name]))
                {
                    List<TestImage> testImages = _testImages.Where(t => _imageEnabled[t.path]).ToList();
                    if (testImages.Count == 0) continue;

                    // Preload first image once since otherwise timings are incorrect due to loading
                    token.ThrowIfCancellationRequested();
                    await RunSingleCaptionTestAsync(model, testImages.First().path);

                    float modelStartTime = Time.realtimeSinceStartup;
                    foreach (TestImage img in testImages)
                    {
                        token.ThrowIfCancellationRequested();
                        await RunSingleCaptionTestAsync(model, img.path);
                    }
                    float modelEndTime = Time.realtimeSinceStartup;
                    _modelTotalTimes[model.Name] = modelEndTime - modelStartTime;
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                _isRunning = false;
                _cts?.Dispose();
                _cts = null;
                Repaint();
            }
        }

        private async Task RunSingleCaptionTestAsync(TestModel model, string imagePath, bool setRunning = false)
        {
            if (setRunning) _isRunning = true;
            int oldBackend = Intelligence.Settings.AIBackend;

            try
            {
                (string m, string i) key = (model.Name, imagePath);
                _results[key] = "Running...";
                Repaint();

                // Temporarily switch backend for this test
                if (Intelligence.Settings is BrainSettings mutableSettings)
                {
                    mutableSettings.aiBackend = model.Backend;
                }

                string prompt = _customPrompt ?? Intelligence.DefaultPrompt;
                // Simple variable replacement for test
                prompt = prompt.Replace("$filename", Path.GetFileName(imagePath));
                prompt = prompt.Replace("$path", imagePath);

                float startTime = Time.realtimeSinceStartup;
                List<CaptionResult> results = await CaptionEngine.CaptionImages(
                    new List<string> {imagePath},
                    new List<string> {prompt},
                    model.Name,
                    null,
                    _cts?.Token ?? default);
                float endTime = Time.realtimeSinceStartup;

                string caption = results?.FirstOrDefault()?.caption ?? string.Empty;
                _results[key] = caption;
                _cellTimes[key] = endTime - startTime;

                float modelTotal = _cellTimes.Where(kv => kv.Key.model == model.Name).Sum(kv => kv.Value);
                _modelTotalTimes[model.Name] = modelTotal;

                Repaint();
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error running single caption test: {ex.Message}");
            }

            // Restore backend
            if (Intelligence.Settings is BrainSettings settings)
            {
                settings.aiBackend = oldBackend;
            }
            if (setRunning) _isRunning = false;
        }

        private List<TestImage> GetTestImages()
        {
            List<TestImage> images = new List<TestImage>();

            if (!string.IsNullOrEmpty(_imagePath) && Directory.Exists(_imagePath))
            {
                string[] files = Directory.GetFiles(_imagePath, "*.png");
                foreach (string file in files)
                {
                    string assetPath = file.Replace("\\", "/");
                    if (assetPath.StartsWith(Application.dataPath))
                    {
                        assetPath = "Assets" + assetPath.Substring(Application.dataPath.Length);
                    }
                    Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                    images.Add(new TestImage {path = file, texture = texture});
                }
            }

            return images;
        }
    }

    [Serializable]
    internal sealed class TestImage
    {
        public string path;
        public Texture2D texture;
    }

    [Serializable]
    internal sealed class TestModel
    {
        public string Name;
        public int Backend; // 0 = BLIP, 1 = Ollama, 2 = LM Studio
    }
}