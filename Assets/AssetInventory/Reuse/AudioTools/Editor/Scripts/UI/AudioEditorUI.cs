using ImpossibleRobert.Common;
using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
#if !AUDIO_TOOL_NOAUDIO
using JD.EditorAudioUtils;
#endif

namespace AudioTool
{
    /// <summary>
    /// Audio editor window for trimming, processing, and exporting audio files.
    /// Can work standalone (via context menu) or embedded (via IAudioSource).
    /// </summary>
    public sealed class AudioEditorUI : CommonEditorUI
    {
        private const int WAVEFORM_HEIGHT = 200;
        private const int HANDLE_WIDTH = 8;

        private string _audioFilePath;
        private string _exportFolder;
        private float _selectionStartNormalized;
        private float _selectionEndNormalized = 1f;
        private bool _hasSelection;
        private bool _loop;
        private bool _playSelection = true;
        private float _silenceThreshold = 0.01f;
        private bool _normalize;
        private float _normalizeTarget = 0.95f;
        private bool _fadeIn;
        private float _fadeInDuration = 1f;
        private bool _fadeOut;
        private float _fadeOutDuration = 1f;
        private AnimationCurve _fadeInCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        private AnimationCurve _fadeOutCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);
        private bool _adjustVolume;
        private float _volumeAdjustment = 1f;
        private bool _showProcessingOptions;

        // Delayed apply for effect changes (debouncing)
        private double _lastEffectChangeTime;
        private bool _pendingEffectApply;
        private const double EFFECT_APPLY_DELAY = 0.15;

        // Cached state that needs to survive domain reload
        private bool _isInProject;
        private string _projectPath;
        private string _fileName;

        // Non-serialized fields need rebuild after recompile
        private IAudioSource _audioSource;
        private AudioClip _audioClip;
        private AudioClip _processedClip;
        private float[] _samples;
        private float[] _originalSamples;
        private bool _isLoading;
        private string _loadingMessage;
        private bool _needsReload;
        private bool _autoPlayOnLoad;

        private AudioWaveformRenderer _waveformRenderer;
        private Texture2D _waveformTexture;
        private Texture2D _overlayTexture;
        private Rect _waveformRect;

        private bool _isDraggingStart;
        private bool _isDraggingEnd;
        private bool _isDraggingSelection;
        private bool _isClickPending;
        private float _clickStartNormalized;
        private float _dragOffset;

        private bool _isPlaying;
        private float _manualPlayheadPosition;

        private float _silenceStartNormalized;
        private float _silenceEndNormalized = 1f;
        private bool _silenceDetected;

        private Vector2 _scrollPosition;

        private float _cachedPeakAmplitude;
        private string _cachedPeakDb;
        private bool _overlayNeedsUpdate = true;
        private float _lastSelectionStart;
        private float _lastSelectionEnd;
        private bool _lastHasSelection;
        private float _lastPlayheadPosition = -1f;

        // Time label animation state
        private const float LABEL_ANIM_SPEED = 14f;
        private const float LABEL_ROW1_Y = 2f;
        private const float LABEL_ROW2_Y = 18f;
        private const float LABEL_ROW3_Y = 34f;
        private const float LABEL_HEIGHT = 16f;
        private const float MIN_LABEL_GAP = 6f;
        private const float TIME_LABEL_WIDTH = 52f; // Fixed width for "0:00.00" format
        private float _currentPosLabelY = LABEL_ROW2_Y;
        private float _durationLabelY = LABEL_ROW2_Y;
        private double _lastAnimTime;

        // Cached GUIStyles for time labels
        private static GUIStyle _selectionMarkerStyle;
        private static GUIStyle _playheadStyle;
        private static GUIStyle _durationStyle;

        private static void EnsureTimeStyles()
        {
            if (_selectionMarkerStyle == null)
            {
                _selectionMarkerStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = new Color(0.3f, 0.7f, 1f) },
                    alignment = TextAnchor.MiddleCenter
                };
            }
            if (_playheadStyle == null)
            {
                _playheadStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = new Color(1f, 0.4f, 0.4f) },
                    alignment = TextAnchor.MiddleCenter
                };
            }
            if (_durationStyle == null)
            {
                _durationStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = new Color(1f, 0.8f, 0.3f) },
                    alignment = TextAnchor.MiddleCenter
                };
            }
        }

        public static AudioEditorUI ShowWindow()
        {
            AudioEditorUI window = GetWindow<AudioEditorUI>("Audio Editor");
            window.minSize = new Vector2(430, 300);
            window.Show();
            return window;
        }

        /// <summary>
        /// Initializes the editor with an audio source.
        /// </summary>
        /// <param name="audioSource">The audio source to edit</param>
        /// <param name="exportFolder">Target folder for export (defaults to same folder as source)</param>
        public async void Init(IAudioSource audioSource, string exportFolder = null)
        {
            _audioSource = audioSource;
            _audioFilePath = audioSource is FileAudioSource fas ? fas.FullPath : null;
            _isInProject = audioSource.IsInProject;
            _projectPath = audioSource.ProjectPath;
            _fileName = audioSource.FileName;
            _waveformRenderer = new AudioWaveformRenderer();

            // Default export folder to same location as source
            if (string.IsNullOrEmpty(exportFolder))
            {
                if (audioSource.IsInProject && !string.IsNullOrEmpty(audioSource.ProjectPath))
                {
                    _exportFolder = Path.GetDirectoryName(IOUtils.PathCombine(Path.GetDirectoryName(Application.dataPath), audioSource.ProjectPath));
                }
                else if (_audioFilePath != null)
                {
                    _exportFolder = Path.GetDirectoryName(_audioFilePath);
                }
            }
            else
            {
                _exportFolder = exportFolder;
            }

            _selectionStartNormalized = 0f;
            _selectionEndNormalized = 1f;
            _hasSelection = false;
            _manualPlayheadPosition = 0f;
            _silenceDetected = false;
            _isPlaying = false;
            _needsReload = false;
            _autoPlayOnLoad = true;

            await LoadAudioClip();
        }

        private async Task LoadAudioClip()
        {
            _isLoading = true;
            _loadingMessage = "Loading audio file...";
            Repaint();

            try
            {
                string targetPath = await _audioSource.GetMaterializedPathAsync();

                if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
                {
                    _loadingMessage = "Failed to load audio file.";
                    return;
                }

                // Load without streaming to access raw samples via GetData()
                _audioClip = await AudioManager.LoadAudioFromFile(targetPath, false);

                if (_audioClip == null)
                {
                    _loadingMessage = "Failed to decode audio file.";
                    return;
                }

                if (!_audioClip.LoadAudioData())
                {
                    _loadingMessage = "Failed to load audio data.";
                    return;
                }

                _samples = new float[_audioClip.samples * _audioClip.channels];
                if (!_audioClip.GetData(_samples, 0))
                {
                    _loadingMessage = "Failed to read audio samples.";
                    return;
                }

                _originalSamples = new float[_samples.Length];
                Array.Copy(_samples, _originalSamples, _samples.Length);

                _loadingMessage = "Rendering waveform...";
                Repaint();

                await ApplyEffects();
                _overlayNeedsUpdate = true;
                _isLoading = false;

                // Auto-play on initial window open (not after recompile)
                if (_autoPlayOnLoad)
                {
                    _autoPlayOnLoad = false;
                    StartPlayback();
                }
            }
            catch (Exception e)
            {
                _loadingMessage = $"Error: {e.Message}";
                Debug.LogError($"AudioEditorUI: {e}");
            }

            Repaint();
        }

        private async System.Threading.Tasks.Task ApplyEffects()
        {
            if (_originalSamples == null || _samples == null || _audioClip == null) return;
            if (_originalSamples.Length != _samples.Length) return;

            bool hasAnyEffect = _normalize || _fadeIn || _fadeOut || _adjustVolume;

            if (!hasAnyEffect && !_hasSelection)
            {
                Array.Copy(_originalSamples, _samples, _originalSamples.Length);
                _cachedPeakAmplitude = AudioProcessor.GetPeakAmplitude(_samples);
                _cachedPeakDb = _cachedPeakAmplitude > 0 ? $"{20 * Mathf.Log10(_cachedPeakAmplitude):F1} dB" : "-∞ dB";

                if (_processedClip != null)
                {
                    DestroyImmediate(_processedClip);
                    _processedClip = null;
                }

                RenderWaveform();
                _overlayNeedsUpdate = true;
                return;
            }

            if (!hasAnyEffect)
            {
                Array.Copy(_originalSamples, _samples, _originalSamples.Length);
                _cachedPeakAmplitude = AudioProcessor.GetPeakAmplitude(_samples);
                _cachedPeakDb = _cachedPeakAmplitude > 0 ? $"{20 * Mathf.Log10(_cachedPeakAmplitude):F1} dB" : "-∞ dB";

                if (_processedClip != null)
                {
                    DestroyImmediate(_processedClip);
                    _processedClip = null;
                }

                RenderWaveform();
                _overlayNeedsUpdate = true;
                return;
            }

            Array.Copy(_originalSamples, _samples, _originalSamples.Length);

            float selStart = _hasSelection ? _selectionStartNormalized : 0f;
            float selEnd = _hasSelection ? _selectionEndNormalized : 1f;

            int startSample = Mathf.RoundToInt(selStart * _audioClip.samples);
            int endSample = Mathf.RoundToInt(selEnd * _audioClip.samples);

            if (startSample >= endSample) return;

            float[] workingSamples = AudioProcessor.TrimAudio(_samples, _audioClip.channels, startSample, endSample);
            if (workingSamples == null || workingSamples.Length == 0) return;

            if (_normalize)
            {
                AudioProcessor.Normalize(workingSamples, _normalizeTarget);
            }

            if (_fadeIn)
            {
                int fadeSamples = Mathf.RoundToInt(_fadeInDuration * _audioClip.frequency);
                AudioProcessor.ApplyFade(workingSamples, _audioClip.channels, fadeSamples, true, _fadeInCurve);
            }

            if (_fadeOut)
            {
                int fadeSamples = Mathf.RoundToInt(_fadeOutDuration * _audioClip.frequency);
                AudioProcessor.ApplyFade(workingSamples, _audioClip.channels, fadeSamples, false, _fadeOutCurve);
            }

            if (_adjustVolume && !Mathf.Approximately(_volumeAdjustment, 1f))
            {
                AudioProcessor.AdjustVolume(workingSamples, _volumeAdjustment);
            }

            int sourceOffset = startSample * _audioClip.channels;
            for (int i = 0; i < workingSamples.Length && (sourceOffset + i) < _samples.Length; i++)
            {
                _samples[sourceOffset + i] = workingSamples[i];
            }

            _cachedPeakAmplitude = AudioProcessor.GetPeakAmplitude(workingSamples);
            _cachedPeakDb = _cachedPeakAmplitude > 0 ? $"{20 * Mathf.Log10(_cachedPeakAmplitude):F1} dB" : "-∞ dB";

            if (_processedClip != null)
            {
                DestroyImmediate(_processedClip);
            }

            _processedClip = await AudioProcessor.CreateClipFromSamples(_samples, _audioClip.channels, _audioClip.frequency, "ProcessedAudio");

            RenderWaveform();
            _overlayNeedsUpdate = true;

            // Auto-restart playback if currently playing to hear changes
            if (_isPlaying)
            {
                StopPlayback();
                StartPlayback();
            }
        }

        private void RenderWaveform()
        {
            if (_audioClip == null || _samples == null) return;
            if (_waveformRenderer == null)
            {
                _waveformRenderer = new AudioWaveformRenderer();
            }

            int width = (int)position.width;
            width = Mathf.Max(400, width - 40);

            _waveformRenderer.RenderWaveform(ref _waveformTexture, _samples, _audioClip.channels, width, WAVEFORM_HEIGHT);
        }

        private void UpdateOverlay(bool forceUpdate = false)
        {
            if (_waveformTexture == null) return;

            float playhead = _isPlaying ? GetNormalizedPlayheadPosition() : _manualPlayheadPosition;

            bool selectionChanged = Mathf.Abs(_selectionStartNormalized - _lastSelectionStart) > 0.0001f ||
                Mathf.Abs(_selectionEndNormalized - _lastSelectionEnd) > 0.0001f ||
                _hasSelection != _lastHasSelection;
            bool playheadChanged = Mathf.Abs(playhead - _lastPlayheadPosition) > 0.001f;
            bool isDragging = _isDraggingStart || _isDraggingEnd || _isDraggingSelection;

            bool needsUpdate = forceUpdate || _overlayNeedsUpdate || selectionChanged || playheadChanged || isDragging;

            if (!needsUpdate) return;

            // Selection values for overlay - only pass actual selection, not the full clip
            float selStart = _hasSelection ? _selectionStartNormalized : 0f;
            float selEnd = _hasSelection ? _selectionEndNormalized : 0f;
            
            // Effect region - where fades apply (selection if exists, else full clip)
            float effectStart = _hasSelection ? _selectionStartNormalized : 0f;
            float effectEnd = _hasSelection ? _selectionEndNormalized : 1f;

            // Calculate fade regions for overlay visualization
            float fadeInEnd = -1f;
            float fadeOutStart = -1f;
            float effectDuration = (effectEnd - effectStart) * _audioClip.length;
            
            if (_fadeIn && _fadeInDuration > 0 && _audioClip != null && effectDuration > 0)
            {
                fadeInEnd = effectStart + (_fadeInDuration / effectDuration) * (effectEnd - effectStart);
                fadeInEnd = Mathf.Min(fadeInEnd, effectEnd);
            }
            if (_fadeOut && _fadeOutDuration > 0 && _audioClip != null && effectDuration > 0)
            {
                fadeOutStart = effectEnd - (_fadeOutDuration / effectDuration) * (effectEnd - effectStart);
                fadeOutStart = Mathf.Max(fadeOutStart, effectStart);
            }

            _waveformRenderer.CreateOverlay(
                ref _overlayTexture,
                _waveformTexture.width,
                _waveformTexture.height,
                selStart,
                selEnd,
                playhead,
                0f,
                1f,
                _fadeIn ? fadeInEnd : -1f,
                _fadeOut ? fadeOutStart : -1f,
                _fadeIn ? _fadeInCurve : null,
                _fadeOut ? _fadeOutCurve : null
            );

            _lastSelectionStart = _selectionStartNormalized;
            _lastSelectionEnd = _selectionEndNormalized;
            _lastHasSelection = _hasSelection;
            _lastPlayheadPosition = playhead;
            _overlayNeedsUpdate = false;
        }

        private void DetectSilence()
        {
            if (_originalSamples == null || _audioClip == null) return;

            (int startSample, int endSample) = AudioProcessor.DetectSilence(_originalSamples, _audioClip.channels, _silenceThreshold);
            int totalSamples = _audioClip.samples;

            _silenceStartNormalized = (float)startSample / totalSamples;
            _silenceEndNormalized = (float)endSample / totalSamples;
            _silenceDetected = true;
            _overlayNeedsUpdate = true;
        }

        private void DetectSilenceInRange(float rangeStartNormalized, float rangeEndNormalized, out float trimmedStartNormalized, out float trimmedEndNormalized)
        {
            trimmedStartNormalized = rangeStartNormalized;
            trimmedEndNormalized = rangeEndNormalized;

            if (_originalSamples == null || _audioClip == null) return;

            int totalSamples = _audioClip.samples;
            int rangeStartSample = Mathf.RoundToInt(rangeStartNormalized * totalSamples);
            int rangeEndSample = Mathf.RoundToInt(rangeEndNormalized * totalSamples);

            float[] rangeSamples = AudioProcessor.TrimAudio(_originalSamples, _audioClip.channels, rangeStartSample, rangeEndSample);
            if (rangeSamples == null || rangeSamples.Length == 0) return;

            (int localStartSample, int localEndSample) = AudioProcessor.DetectSilence(rangeSamples, _audioClip.channels, _silenceThreshold);

            int rangeLength = rangeEndSample - rangeStartSample;
            if (rangeLength <= 0) return;

            trimmedStartNormalized = rangeStartNormalized + (float)localStartSample / totalSamples;
            trimmedEndNormalized = rangeStartNormalized + (float)localEndSample / totalSamples;
        }

        private float GetNormalizedPlayheadPosition()
        {
#if !AUDIO_TOOL_NOAUDIO
            AudioClip clipToUse = _processedClip != null ? _processedClip : _audioClip;
            if (clipToUse == null || !AudioManager.IsPlaying()) return -1f;

            float currentPos = AudioManager.GetCurrentPosition();
            return currentPos / clipToUse.length;
#else
            return -1f;
#endif
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;

            // Only need to reload if we have a file path but lost the audio clip AND waveform
            // If waveform texture survived domain reload, we can continue working
            if (!string.IsNullOrEmpty(_audioFilePath) && _audioClip == null && _waveformTexture == null && !_isLoading)
            {
                _needsReload = true;
            }

            if (_waveformRenderer == null)
            {
                _waveformRenderer = new AudioWaveformRenderer();
            }
        }

        private async void ReloadAfterRecompile()
        {
            _isLoading = true;
            _loadingMessage = "Restoring audio editor...";
            Repaint();

            if (string.IsNullOrEmpty(_audioFilePath))
            {
                _loadingMessage = "No audio file to reload.";
                _isLoading = false;
                return;
            }

            _audioSource = new FileAudioSource(_audioFilePath);

            if (_waveformRenderer == null)
            {
                _waveformRenderer = new AudioWaveformRenderer();
            }

            await LoadAudioClip();
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            AudioManager.StopAudio();
            _waveformRenderer = null;
            
            // Only destroy textures and processed clip when actually closing the window,
            // not during domain reload (script recompilation)
            bool isDomainReload = EditorApplication.isCompiling || EditorApplication.isUpdating;
            if (!isDomainReload)
            {
                if (_waveformTexture != null)
                {
                    DestroyImmediate(_waveformTexture);
                    _waveformTexture = null;
                }
                if (_overlayTexture != null)
                {
                    DestroyImmediate(_overlayTexture);
                    _overlayTexture = null;
                }
                if (_processedClip != null)
                {
                    DestroyImmediate(_processedClip);
                    _processedClip = null;
                }
            }
        }

        private void OnEditorUpdate()
        {
            // Handle delayed apply for effect changes (debouncing)
            if (_pendingEffectApply && EditorApplication.timeSinceStartup - _lastEffectChangeTime >= EFFECT_APPLY_DELAY)
            {
                _pendingEffectApply = false;
                _ = ApplyEffects();
            }

            if (_isPlaying)
            {
#if !AUDIO_TOOL_NOAUDIO
                if (!AudioManager.IsPlaying())
                {
                    if (_loop && _playSelection && _hasSelection)
                    {
                        PlaySelection();
                    }
                    else
                    {
                        _isPlaying = false;
                    }
                }
                else if (_playSelection && _hasSelection && AudioManager.IsRangePlaying)
                {
                    if (AudioManager.HasReachedRangeEnd())
                    {
                        if (_loop)
                        {
                            PlaySelection();
                        }
                        else
                        {
                            AudioManager.StopAudio();
                            _isPlaying = false;
                        }
                    }
                }
#endif
                Repaint();
            }
        }

        private void OnGUI()
        {
            if (_needsReload && !_isLoading)
            {
                _needsReload = false;
                ReloadAfterRecompile();
                return;
            }

            if (_isLoading || _audioClip == null)
            {
                DrawLoadingState();
                return;
            }

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            DrawHeader();
            EditorGUILayout.Space(10);
            DrawWaveform();
            EditorGUILayout.Space(10);
            DrawTransportControls();
            EditorGUILayout.Space(10);
            DrawProcessingOptions();

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(10);
            DrawExportSection();
        }

        private void DrawLoadingState()
        {
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(_loadingMessage, EditorStyles.centeredGreyMiniLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginVertical("box");
            int labelWidth = 100;

            GUILabelWithText("File", _fileName ?? "Unknown", labelWidth);
            GUILabelWithText("Duration", FormatTime(_audioClip.length), labelWidth);
            GUILabelWithText("Channels", _audioClip.channels == 1 ? "Mono" : "Stereo", labelWidth);
            GUILabelWithText("Sample Rate", $"{_audioClip.frequency} Hz", labelWidth);
            GUILabelWithText("Peak Level", $"{_cachedPeakAmplitude:P1} ({_cachedPeakDb})", labelWidth);

            EditorGUILayout.EndVertical();
        }

        private void DrawWaveform()
        {
            int expectedWidth = Mathf.Max(400, (int)position.width - 40);
            if (_waveformTexture != null && _waveformTexture.width != expectedWidth)
            {
                RenderWaveform();
                _overlayNeedsUpdate = true;
            }

            _waveformRect = GUILayoutUtility.GetRect(expectedWidth, WAVEFORM_HEIGHT + 52);
            _waveformRect.x += 10;
            _waveformRect.width -= 20;
            _waveformRect.height = WAVEFORM_HEIGHT;

            HandleWaveformInput();
            UpdateOverlay();

            EditorGUI.DrawRect(_waveformRect, new Color(0.1f, 0.1f, 0.1f));

            if (_waveformTexture != null)
            {
                GUI.DrawTexture(_waveformRect, _waveformTexture, ScaleMode.StretchToFill);
            }

            if (_overlayTexture != null)
            {
                GUI.DrawTexture(_waveformRect, _overlayTexture, ScaleMode.StretchToFill);
            }

            DrawTimeLabels();
        }

        private void DrawTimeLabels()
        {
            EnsureTimeStyles();

            double currentTime = EditorApplication.timeSinceStartup;
            float deltaTime = (float)(currentTime - _lastAnimTime);
            _lastAnimTime = currentTime;

            deltaTime = Mathf.Clamp(deltaTime, 0f, 0.1f);

            Rect timeLabelRect = new Rect(_waveformRect.x, _waveformRect.yMax + LABEL_ROW1_Y, TIME_LABEL_WIDTH, LABEL_HEIGHT);
            EditorGUI.LabelField(timeLabelRect, "0:00", EditorStyles.miniLabel);

            string totalDurationText = FormatTime(_audioClip.length);
            timeLabelRect = new Rect(_waveformRect.xMax - TIME_LABEL_WIDTH, _waveformRect.yMax + LABEL_ROW1_Y, TIME_LABEL_WIDTH, LABEL_HEIGHT);
            EditorGUI.LabelField(timeLabelRect, totalDurationText, EditorStyles.miniLabel);

            if (_hasSelection)
            {
                float startX = _waveformRect.x + _selectionStartNormalized * _waveformRect.width;
                float endX = _waveformRect.x + _selectionEndNormalized * _waveformRect.width;

                string startText = FormatTime(_selectionStartNormalized * _audioClip.length);
                string endText = FormatTime(_selectionEndNormalized * _audioClip.length);

                float startLabelX = startX - TIME_LABEL_WIDTH / 2;
                float endLabelX = endX - TIME_LABEL_WIDTH / 2;

                startLabelX = Mathf.Max(startLabelX, _waveformRect.x + TIME_LABEL_WIDTH + MIN_LABEL_GAP);
                endLabelX = Mathf.Min(endLabelX, _waveformRect.xMax - TIME_LABEL_WIDTH * 2 - MIN_LABEL_GAP);

                if (startLabelX + TIME_LABEL_WIDTH + MIN_LABEL_GAP > endLabelX)
                {
                    float center = (startX + endX) / 2;
                    startLabelX = center - TIME_LABEL_WIDTH - MIN_LABEL_GAP / 2;
                    endLabelX = center + MIN_LABEL_GAP / 2;
                }

                timeLabelRect = new Rect(startLabelX, _waveformRect.yMax + LABEL_ROW1_Y, TIME_LABEL_WIDTH, LABEL_HEIGHT);
                EditorGUI.LabelField(timeLabelRect, startText, _selectionMarkerStyle);

                timeLabelRect = new Rect(endLabelX, _waveformRect.yMax + LABEL_ROW1_Y, TIME_LABEL_WIDTH, LABEL_HEIGHT);
                EditorGUI.LabelField(timeLabelRect, endText, _selectionMarkerStyle);
            }

            float currentPlayhead = _isPlaying ? GetNormalizedPlayheadPosition() : _manualPlayheadPosition;

            Rect currentPosRect = Rect.zero;
            Rect durationRect = Rect.zero;
            string currentPosText = "";
            string durationText = "";
            float durationCenterX = 0f;
            float playheadX = 0f;

            bool showCurrentPos = currentPlayhead >= 0;
            bool showDuration = _hasSelection;

            if (showCurrentPos)
            {
                playheadX = _waveformRect.x + currentPlayhead * _waveformRect.width;
                currentPosText = FormatTime(currentPlayhead * _audioClip.length);
                float currentPosX = Mathf.Clamp(playheadX - TIME_LABEL_WIDTH / 2, _waveformRect.x, _waveformRect.xMax - TIME_LABEL_WIDTH);
                currentPosRect = new Rect(currentPosX, _waveformRect.yMax + _currentPosLabelY, TIME_LABEL_WIDTH, LABEL_HEIGHT);
            }

            if (showDuration)
            {
                float startX = _waveformRect.x + _selectionStartNormalized * _waveformRect.width;
                float endX = _waveformRect.x + _selectionEndNormalized * _waveformRect.width;
                float selectionDuration = (_selectionEndNormalized - _selectionStartNormalized) * _audioClip.length;
                durationCenterX = (startX + endX) / 2;
                durationText = FormatTime(selectionDuration);
                durationRect = new Rect(durationCenterX - TIME_LABEL_WIDTH / 2, _waveformRect.yMax + _durationLabelY, TIME_LABEL_WIDTH, LABEL_HEIGHT);
            }

            float targetPosY = showDuration ? LABEL_ROW3_Y : LABEL_ROW2_Y;

            bool needsRepaint = false;
            if (Mathf.Abs(_currentPosLabelY - targetPosY) > 0.5f)
            {
                _currentPosLabelY = Mathf.Lerp(_currentPosLabelY, targetPosY, deltaTime * LABEL_ANIM_SPEED);
                needsRepaint = true;
            }
            else
            {
                _currentPosLabelY = targetPosY; // Snap to exact row value
            }

            if (showCurrentPos)
            {
                currentPosRect.y = _waveformRect.yMax + _currentPosLabelY;

                if (targetPosY > LABEL_ROW2_Y && _currentPosLabelY > LABEL_ROW2_Y + 1f)
                {
                    float lineX = playheadX;
                    float lineTop = _waveformRect.yMax + LABEL_ROW2_Y;
                    float lineBottom = currentPosRect.y;

                    Color lineColor = new Color(1f, 0.4f, 0.4f, 0.5f);
                    DrawDottedLine(lineX, lineTop, lineBottom, lineColor);
                }

                EditorGUI.LabelField(currentPosRect, currentPosText, _playheadStyle);
            }

            if (showDuration)
            {
                durationRect.y = _waveformRect.yMax + _durationLabelY;

                if (_durationLabelY > LABEL_ROW2_Y + 1f)
                {
                    float lineX = durationCenterX;
                    float lineTop = _waveformRect.yMax + LABEL_ROW2_Y;
                    float lineBottom = durationRect.y;

                    Color lineColor = new Color(1f, 0.8f, 0.3f, 0.5f);
                    DrawDottedLine(lineX, lineTop, lineBottom, lineColor);
                }

                EditorGUI.LabelField(durationRect, durationText, _durationStyle);
            }

            if (needsRepaint || _isPlaying)
            {
                Repaint();
            }
        }

        private void DrawDottedLine(float x, float top, float bottom, Color color)
        {
            const float dotLength = 2f;
            const float gapLength = 2f;

            float y = top;
            while (y < bottom)
            {
                float segmentEnd = Mathf.Min(y + dotLength, bottom);
                EditorGUI.DrawRect(new Rect(x - 0.5f, y, 1f, segmentEnd - y), color);
                y = segmentEnd + gapLength;
            }
        }

        private void HandleWaveformInput()
        {
            Event e = Event.current;
            if (!_waveformRect.Contains(e.mousePosition) && !_isDraggingStart && !_isDraggingEnd && !_isDraggingSelection)
            {
                return;
            }

            Rect startHandleRect = Rect.zero;
            Rect endHandleRect = Rect.zero;

            if (_hasSelection)
            {
                float startX = _waveformRect.x + _selectionStartNormalized * _waveformRect.width;
                float endX = _waveformRect.x + _selectionEndNormalized * _waveformRect.width;

                startHandleRect = new Rect(startX - HANDLE_WIDTH / 2f, _waveformRect.y, HANDLE_WIDTH, _waveformRect.height);
                endHandleRect = new Rect(endX - HANDLE_WIDTH / 2f, _waveformRect.y, HANDLE_WIDTH, _waveformRect.height);

                if (startHandleRect.Contains(e.mousePosition) || endHandleRect.Contains(e.mousePosition))
                {
                    EditorGUIUtility.AddCursorRect(startHandleRect, MouseCursor.ResizeHorizontal);
                    EditorGUIUtility.AddCursorRect(endHandleRect, MouseCursor.ResizeHorizontal);
                }
            }

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (e.button == 0)
                    {
                        if (_hasSelection && startHandleRect.Contains(e.mousePosition))
                        {
                            _isDraggingStart = true;
                            _isClickPending = false;
                            _overlayNeedsUpdate = true;
                            e.Use();
                        }
                        else if (_hasSelection && endHandleRect.Contains(e.mousePosition))
                        {
                            _isDraggingEnd = true;
                            _isClickPending = false;
                            _overlayNeedsUpdate = true;
                            e.Use();
                        }
                        else if (_waveformRect.Contains(e.mousePosition))
                        {
                            float clickNorm = (e.mousePosition.x - _waveformRect.x) / _waveformRect.width;
                            _clickStartNormalized = clickNorm;

                            bool clickInsideSelection = _hasSelection &&
                                clickNorm >= _selectionStartNormalized &&
                                clickNorm <= _selectionEndNormalized;

                            if (clickInsideSelection)
                            {
                                _isDraggingSelection = true;
                                _dragOffset = clickNorm - _selectionStartNormalized;
                                _isClickPending = true;
                            }
                            else
                            {
                                _isClickPending = true;
                            }
                            e.Use();
                        }
                    }
                    break;

                case EventType.MouseDrag:
                    if (_waveformRect.width > 0)
                    {
                        float normalized = Mathf.Clamp01((e.mousePosition.x - _waveformRect.x) / _waveformRect.width);
                        float dragDistance = Mathf.Abs(normalized - _clickStartNormalized);

                        bool justStartedNewSelection = false;
                        if (_isClickPending && dragDistance > 0.005f)
                        {
                            _isClickPending = false;

                            if (!_isDraggingSelection)
                            {
                                _selectionStartNormalized = _clickStartNormalized;
                                _selectionEndNormalized = _clickStartNormalized;
                                _hasSelection = true;
                                _isDraggingEnd = true;
                                justStartedNewSelection = true;
                            }
                        }

                        if (_isDraggingStart || _isDraggingEnd || _isDraggingSelection)
                        {
                            float oldStart = _selectionStartNormalized;
                            float oldEnd = _selectionEndNormalized;

                            if (_isDraggingStart)
                            {
                                _selectionStartNormalized = Mathf.Min(normalized, _selectionEndNormalized - 0.001f);
                            }
                            else if (_isDraggingEnd)
                            {
                                _selectionEndNormalized = Mathf.Max(normalized, _selectionStartNormalized + 0.001f);
                            }
                            else if (_isDraggingSelection && !_isClickPending)
                            {
                                float selectionWidth = _selectionEndNormalized - _selectionStartNormalized;
                                float newStart = Mathf.Clamp(normalized - _dragOffset, 0, 1 - selectionWidth);
                                _selectionStartNormalized = newStart;
                                _selectionEndNormalized = newStart + selectionWidth;
                            }

                            bool selectionChanged = Mathf.Abs(_selectionStartNormalized - oldStart) > 0.001f ||
                                Mathf.Abs(_selectionEndNormalized - oldEnd) > 0.001f;
                            if (_isPlaying && _playSelection && _hasSelection &&
                                (justStartedNewSelection || selectionChanged))
                            {
                                int startSample = Mathf.RoundToInt(_selectionStartNormalized * _audioClip.samples);
                                int endSample = Mathf.RoundToInt(_selectionEndNormalized * _audioClip.samples);
                                AudioManager.PlayClipRange(_audioClip, startSample, endSample, _loop);
                            }

                            _overlayNeedsUpdate = true;
                            e.Use();
                            Repaint();
                        }
                    }
                    break;

                case EventType.MouseUp:
                    if (_isClickPending)
                    {
                        bool hadSelection = _hasSelection;
                        _manualPlayheadPosition = _clickStartNormalized;
                        _hasSelection = false;

                        if (hadSelection)
                        {
                            _ = ApplyEffects();
                        }
                        _overlayNeedsUpdate = true;
                        _isClickPending = false;
                        _isDraggingSelection = false;
                        e.Use();
                        Repaint();
                    }
                    else if (_isDraggingStart || _isDraggingEnd || _isDraggingSelection)
                    {
                        if (_selectionStartNormalized > _selectionEndNormalized)
                        {
                            float temp = _selectionStartNormalized;
                            _selectionStartNormalized = _selectionEndNormalized;
                            _selectionEndNormalized = temp;
                        }

                        _manualPlayheadPosition = _selectionStartNormalized;

                        _isDraggingStart = false;
                        _isDraggingEnd = false;
                        _isDraggingSelection = false;
                        _ = ApplyEffects();
                        _overlayNeedsUpdate = true;
                        e.Use();
                        Repaint();
                    }
                    break;

                case EventType.KeyDown:
                    if (e.keyCode == KeyCode.Space)
                    {
                        if (_isPlaying)
                        {
                            StopPlayback();
                        }
                        else
                        {
                            StartPlayback();
                        }
                        e.Use();
                    }
                    break;
            }
        }

        private void DrawTransportControls()
        {
            EditorGUILayout.LabelField("Playback", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal(CommonUIStyles.sectionBox);

            float currentPlayhead = _isPlaying ? GetNormalizedPlayheadPosition() : _manualPlayheadPosition;
            bool showRewind = false;
            if (!_isPlaying)
            {
                if (_playSelection && _hasSelection)
                {
                    showRewind = Mathf.Abs(currentPlayhead - _selectionStartNormalized) > 0.0001f;
                }
                else
                {
                    showRewind = currentPlayhead > 0.0001f;
                }
            }

            if (GUILayout.Button(EditorGUIUtility.IconContent(_isPlaying ? "d_PreMatQuad" : "d_PlayButton", _isPlaying ? "|Stop" : "|Play"), GUILayout.Width(40)))
            {
                if (_isPlaying)
                {
                    StopPlayback();
                }
                else
                {
                    StartPlayback();
                }
            }
            if (showRewind)
            {
                if (GUILayout.Button(EditorGUIUtility.IconContent("d_Animation.PrevKey", "|Rewind to Start"), GUILayout.Width(40)))
                {
                    if (_isPlaying)
                    {
                        StopPlayback();
                    }
                    _manualPlayheadPosition = 0f;
                    _overlayNeedsUpdate = true;
                    Repaint();
                }
            }

            if (GUILayout.Button("Select Trimmed", GUILayout.Height(20)))
            {
                bool wasPlaying = _isPlaying;
                if (wasPlaying)
                {
                    StopPlayback();
                }

                float oldPlayhead = _manualPlayheadPosition;

                if (_hasSelection)
                {
                    DetectSilenceInRange(_selectionStartNormalized, _selectionEndNormalized, out float trimmedStart, out float trimmedEnd);
                    _selectionStartNormalized = trimmedStart;
                    _selectionEndNormalized = trimmedEnd;

                    if (oldPlayhead < trimmedStart || oldPlayhead > trimmedEnd)
                    {
                        _manualPlayheadPosition = trimmedStart;
                    }
                }
                else
                {
                    if (!_silenceDetected)
                    {
                        DetectSilence();
                    }
                    _selectionStartNormalized = _silenceStartNormalized;
                    _selectionEndNormalized = _silenceEndNormalized;
                    _hasSelection = true;

                    if (oldPlayhead < _silenceStartNormalized || oldPlayhead > _silenceEndNormalized)
                    {
                        _manualPlayheadPosition = _silenceStartNormalized;
                    }
                }

                if (wasPlaying)
                {
                    StartPlayback();
                }

                _ = ApplyEffects();
                _overlayNeedsUpdate = true;
            }
            if (_hasSelection && GUILayout.Button("Clear Selection", GUILayout.Height(20)))
            {
                _hasSelection = false;
                _ = ApplyEffects();
                _overlayNeedsUpdate = true;
            }

            EditorGUILayout.Space(10);

            _loop = GUILayout.Toggle(_loop, "Loop", GUILayout.Width(60));
            if (_hasSelection)
            {
                _playSelection = GUILayout.Toggle(_playSelection, "Selection Only", GUILayout.Width(130));
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawProcessingOptions()
        {
            _showProcessingOptions = EditorGUILayout.Foldout(_showProcessingOptions, "Processing Options", true);
            if (!_showProcessingOptions) return;

            EditorGUILayout.BeginVertical("box");

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _normalize = EditorGUILayout.Toggle(_normalize, GUILayout.Width(20));
            EditorGUILayout.LabelField("Normalize Audio", GUILayout.Width(120));
            EditorGUI.BeginDisabledGroup(!_normalize);
            EditorGUILayout.LabelField("Target:", GUILayout.Width(50));
            _normalizeTarget = EditorGUILayout.Slider(_normalizeTarget, 0.5f, 1f);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
            if (EditorGUI.EndChangeCheck())
            {
                _lastEffectChangeTime = EditorApplication.timeSinceStartup;
                _pendingEffectApply = true;
            }

            EditorGUILayout.Space(5);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _fadeIn = EditorGUILayout.Toggle(_fadeIn, GUILayout.Width(20));
            EditorGUILayout.LabelField("Fade In", GUILayout.Width(120));

            EditorGUI.BeginDisabledGroup(!_fadeIn);
            EditorGUILayout.LabelField("Duration:", GUILayout.Width(60));
            _fadeInDuration = EditorGUILayout.Slider(_fadeInDuration, 0.01f, 5f);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
            if (EditorGUI.EndChangeCheck())
            {
                _lastEffectChangeTime = EditorApplication.timeSinceStartup;
                _pendingEffectApply = true;
            }

            if (_fadeIn)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.Space(20);
                EditorGUILayout.LabelField("Curve:", GUILayout.Width(100));
                EditorGUI.BeginChangeCheck();
                _fadeInCurve = EditorGUILayout.CurveField(_fadeInCurve, GUILayout.Height(30));
                if (EditorGUI.EndChangeCheck())
                {
                    _lastEffectChangeTime = EditorApplication.timeSinceStartup;
                    _pendingEffectApply = true;
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _fadeOut = EditorGUILayout.Toggle(_fadeOut, GUILayout.Width(20));
            EditorGUILayout.LabelField("Fade Out", GUILayout.Width(120));

            EditorGUI.BeginDisabledGroup(!_fadeOut);
            EditorGUILayout.LabelField("Duration:", GUILayout.Width(60));
            _fadeOutDuration = EditorGUILayout.Slider(_fadeOutDuration, 0.01f, 5f);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
            if (EditorGUI.EndChangeCheck())
            {
                _lastEffectChangeTime = EditorApplication.timeSinceStartup;
                _pendingEffectApply = true;
            }

            if (_fadeOut)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.Space(20);
                EditorGUILayout.LabelField("Curve:", GUILayout.Width(100));
                EditorGUI.BeginChangeCheck();
                _fadeOutCurve = EditorGUILayout.CurveField(_fadeOutCurve, GUILayout.Height(30));
                if (EditorGUI.EndChangeCheck())
                {
                    _lastEffectChangeTime = EditorApplication.timeSinceStartup;
                    _pendingEffectApply = true;
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(5);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _adjustVolume = EditorGUILayout.Toggle(_adjustVolume, GUILayout.Width(20));
            EditorGUILayout.LabelField("Adjust Volume", GUILayout.Width(120));

            EditorGUI.BeginDisabledGroup(!_adjustVolume);
            _volumeAdjustment = EditorGUILayout.Slider(_volumeAdjustment, 0f, 2f);
            EditorGUILayout.LabelField($"{(_volumeAdjustment * 100):F0}%", GUILayout.Width(45));
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
            if (EditorGUI.EndChangeCheck())
            {
                _lastEffectChangeTime = EditorApplication.timeSinceStartup;
                _pendingEffectApply = true;
            }

            EditorGUILayout.Space(5);

            if (GUILayout.Button("Reset All", GUILayout.ExpandWidth(false)))
            {
                ResetToInitialState();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawExportSection()
        {
            // Use cached _isInProject since _audioSource may be null after domain reload
            bool isInProject = _isInProject;

            if (isInProject)
            {
                bool isWavFile = !string.IsNullOrEmpty(_fileName) && 
                    _fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);

                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginDisabledGroup(!isWavFile);
                GUIContent overrideContent = isWavFile 
                    ? new GUIContent("Override Existing") 
                    : new GUIContent("Override Existing", "For now only available for WAV files. Use 'Save As Copy' to export as WAV.");
                if (GUILayout.Button(overrideContent, CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
                {
                    PerformExport(true);
                }
                EditorGUI.EndDisabledGroup();
                if (GUILayout.Button("Save As Copy", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
                {
                    PerformExport(false);
                }
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                if (GUILayout.Button("Import", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
                {
                    PerformExport(false);
                }
            }
        }

        private void ResetToInitialState()
        {
            _selectionStartNormalized = 0f;
            _selectionEndNormalized = 1f;
            _hasSelection = false;
            _manualPlayheadPosition = 0f;

            _silenceThreshold = 0.01f;
            _normalize = false;
            _normalizeTarget = 0.95f;
            _fadeIn = false;
            _fadeInDuration = 1f;
            _fadeOut = false;
            _fadeOutDuration = 1f;
            _fadeInCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
            _fadeOutCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);
            _adjustVolume = false;
            _volumeAdjustment = 1f;
            _pendingEffectApply = false;

            _ = ApplyEffects();

            if (_isPlaying)
            {
                StopPlayback();
            }

            _overlayNeedsUpdate = true;
            Repaint();
        }

        private void StartPlayback()
        {
            if (_audioClip == null) return;

            AudioClip clipToPlay = _processedClip != null ? _processedClip : _audioClip;

            if (clipToPlay == null)
            {
                Debug.LogError("StartPlayback: No valid clip to play, falling back to original");
                clipToPlay = _audioClip;
                if (clipToPlay == null) return;
            }

            if (clipToPlay.samples <= 0)
            {
                Debug.LogError($"StartPlayback: Clip has no samples. ProcessedClip null: {_processedClip == null}, falling back to original");
                clipToPlay = _audioClip;
                if (clipToPlay == null || clipToPlay.samples <= 0) return;
            }

            if (clipToPlay.loadState != AudioDataLoadState.Loaded)
            {
                Debug.LogWarning($"StartPlayback: Clip loadState is {clipToPlay.loadState}, attempting to load...");
                if (!clipToPlay.LoadAudioData())
                {
                    Debug.LogError("StartPlayback: Failed to load audio data, falling back to original");
                    clipToPlay = _audioClip;
                }
            }

            _isPlaying = true;

            if (_playSelection && _hasSelection)
            {
                int startSample = Mathf.RoundToInt(_selectionStartNormalized * clipToPlay.samples);
                int endSample = Mathf.RoundToInt(_selectionEndNormalized * clipToPlay.samples);
                AudioManager.PlayClipRange(clipToPlay, startSample, endSample, _loop);
            }
            else
            {
                int startSample = Mathf.RoundToInt(_manualPlayheadPosition * clipToPlay.samples);
                AudioManager.PlayClip(clipToPlay, startSample, _loop);
            }
        }

        private void PlaySelection()
        {
            int startSample = Mathf.RoundToInt(_selectionStartNormalized * _audioClip.samples);
            int endSample = Mathf.RoundToInt(_selectionEndNormalized * _audioClip.samples);
            AudioManager.PlayClipRange(_audioClip, startSample, endSample, false);
        }

        private void StopPlayback()
        {
            _isPlaying = false;
            AudioManager.StopAudio();
        }

        private void PerformExport(bool overrideOriginal)
        {
            if (_audioClip == null || _samples == null) return;

            try
            {
                float selStart = _hasSelection ? _selectionStartNormalized : 0f;
                float selEnd = _hasSelection ? _selectionEndNormalized : 1f;

                int startSample = Mathf.RoundToInt(selStart * _audioClip.samples);
                int endSample = Mathf.RoundToInt(selEnd * _audioClip.samples);

                float[] processedSamples = AudioProcessor.TrimAudio(_samples, _audioClip.channels, startSample, endSample);

                if (processedSamples == null || processedSamples.Length == 0)
                {
                    EditorUtility.DisplayDialog("Error", "No audio data to export. Please adjust your selection.", "OK");
                    return;
                }

                string outputPath;

                if (overrideOriginal && _isInProject)
                {
                    outputPath = IOUtils.PathCombine(Path.GetDirectoryName(Application.dataPath), _projectPath);

                    if (!outputPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    {
                        outputPath = Path.ChangeExtension(outputPath, ".wav");
                    }
                }
                else
                {
                    outputPath = AudioProcessor.GenerateUniqueFilename(_exportFolder, _fileName ?? "audio.wav");
                }

                AudioProcessor.ExportToWav(outputPath, processedSamples, _audioClip.channels, _audioClip.frequency);

                AssetDatabase.Refresh();

                string normalizedOutput = outputPath.Replace('\\', '/');
                string normalizedDataPath = Application.dataPath.Replace('\\', '/');
                
                string relativePath;
                if (normalizedOutput.StartsWith(normalizedDataPath, StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = "Assets" + normalizedOutput.Substring(normalizedDataPath.Length);
                }
                else
                {
                    relativePath = normalizedOutput;
                }

                UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(relativePath);
                if (asset != null)
                {
                    EditorGUIUtility.PingObject(asset);
                    Selection.activeObject = asset;
                }

                Close();
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Error", $"Failed to export audio:\n{e.Message}", "OK");
                Debug.LogError($"AudioEditorUI export error: {e}");
            }
        }

        private string FormatTime(float seconds)
        {
            if (seconds < 0) seconds = 0;
            int minutes = (int)(seconds / 60);
            float secs = seconds % 60;
            return $"{minutes}:{secs:00.00}";
        }
    }
}
