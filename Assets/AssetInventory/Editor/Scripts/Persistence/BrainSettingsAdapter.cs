using Brain;

namespace AssetInventory
{
    /// <summary>
    /// Adapter that exposes AssetInventorySettings as IBrainSettings for the Brain package.
    /// </summary>
    public sealed class BrainSettingsAdapter : IBrainSettings
    {
        private readonly AssetInventorySettings _settings;

        public BrainSettingsAdapter(AssetInventorySettings settings)
        {
            _settings = settings;
        }

        // Ollama settings
        public string OllamaModel => _settings.ollamaModel;

        public string OllamaServiceUrl => string.IsNullOrWhiteSpace(_settings.ollamaServiceUrl)
            ? BrainSettings.DEFAULT_OLLAMA_SERVICE_URL
            : _settings.ollamaServiceUrl;

        public int OllamaParallelRequests => _settings.ollamaParallelRequests;

        // LM Studio settings
        public string LMStudioModel => _settings.lmStudioModel;

        public string LMStudioServiceUrl => string.IsNullOrWhiteSpace(_settings.lmStudioServiceUrl)
            ? BrainSettings.DEFAULT_LMSTUDIO_SERVICE_URL
            : _settings.lmStudioServiceUrl;

        public int LMStudioParallelRequests => _settings.lmStudioParallelRequests;

        // BLIP settings
        public int BlipType => _settings.blipType;
        public bool BlipUseGPU => _settings.blipUseGPU;
        public string BlipPath => _settings.blipPath;
        public int BlipChunkSize => _settings.blipChunkSize;

        // General AI settings
        public int AIBackend => _settings.aiBackend;
        public int AIMinSize => _settings.aiMinSize;
        public int AIMaxCaptionLength => _settings.aiMaxCaptionLength;
        public float AIPause => _settings.aiPause;
        public int AITimeout => _settings.aiTimeout;
        public bool LogAICaptions => _settings.logAICaptions;
        public bool AIContinueOnEmpty => _settings.aiContinueOnEmpty;
    }
}