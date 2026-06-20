using System;

namespace Brain
{
    /// <summary>
    /// Configuration interface for Brain AI services.
    /// </summary>
    public interface IBrainSettings
    {
        // Ollama settings
        string OllamaModel { get; }
        string OllamaServiceUrl { get; }
        int OllamaParallelRequests { get; }

        // LM Studio settings
        string LMStudioModel { get; }
        string LMStudioServiceUrl { get; }
        int LMStudioParallelRequests { get; }

        // BLIP settings
        int BlipType { get; } // 0 = small, 1 = large
        bool BlipUseGPU { get; }
        string BlipPath { get; }
        int BlipChunkSize { get; }

        // General AI settings
        int AIBackend { get; } // 0 = BLIP, 1 = Ollama, 2 = LM Studio
        int AIMinSize { get; } // Minimum image size for AI processing
        int AIMaxCaptionLength { get; }
        float AIPause { get; } // Pause between requests to prevent overheating
        int AITimeout { get; } // Per-request timeout in seconds; 0 = no timeout
        bool LogAICaptions { get; }
        bool AIContinueOnEmpty { get; }
    }

    /// <summary>
    /// Wrapper for AI model information, abstracted from backend-specific types.
    /// </summary>
    public class ModelInfo
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public DateTime? ModifiedAt { get; set; }
        public long Size { get; set; }
        public string Family { get; set; }
        public string ParameterSize { get; set; }
        public string QuantizationLevel { get; set; }
    }

    /// <summary>
    /// Status information for model downloads.
    /// </summary>
    public class DownloadStatus
    {
        public string Status { get; set; }
        public string Digest { get; set; }
        public long Total { get; set; }
        public long Completed { get; set; }
        public float Progress => Total > 0 ? (float)Completed / Total : 0f;
    }

    /// <summary>
    /// Default IBrainSettings implementation.
    /// </summary>
    [Serializable]
    public class BrainSettings : IBrainSettings
    {
        public const string DEFAULT_OLLAMA_SERVICE_URL = "http://localhost:11434";
        public const string DEFAULT_LMSTUDIO_SERVICE_URL = "http://localhost:1234";

        // Ollama
        public string ollamaModel = "qwen2.5vl";
        public string ollamaServiceUrl = DEFAULT_OLLAMA_SERVICE_URL;
        // Ollama serves up to OLLAMA_NUM_PARALLEL requests concurrently per model
        // (defaults to 4 in recent versions). Pushing past that just queues server-side.
        public int ollamaParallelRequests = 4;

        // LM Studio
        public string lmStudioModel = "qwen/qwen2.5-vl-7b";
        public string lmStudioServiceUrl = DEFAULT_LMSTUDIO_SERVICE_URL;
        public int lmStudioParallelRequests = 20;

        // BLIP
        public int blipType; // 0 = small, 1 = large
        public bool blipUseGPU;
        public string blipPath;
        public int blipChunkSize = 1;

        // General
        public int aiBackend = 1; // Default to Ollama
        public int aiMinSize = 32;
        public int aiMaxCaptionLength = 200;
        public float aiPause;
        public int aiTimeout; // 0 = no timeout
        public bool logAICaptions;
        public bool aiContinueOnEmpty;

        // IBrainSettings implementation
        public string OllamaModel => ollamaModel;
        public string OllamaServiceUrl => string.IsNullOrWhiteSpace(ollamaServiceUrl) ? DEFAULT_OLLAMA_SERVICE_URL : ollamaServiceUrl;
        public int OllamaParallelRequests => ollamaParallelRequests;
        public string LMStudioModel => lmStudioModel;
        public string LMStudioServiceUrl => string.IsNullOrWhiteSpace(lmStudioServiceUrl) ? DEFAULT_LMSTUDIO_SERVICE_URL : lmStudioServiceUrl;
        public int LMStudioParallelRequests => lmStudioParallelRequests;
        public int BlipType => blipType;
        public bool BlipUseGPU => blipUseGPU;
        public string BlipPath => blipPath;
        public int BlipChunkSize => blipChunkSize;
        public int AIBackend => aiBackend;
        public int AIMinSize => aiMinSize;
        public int AIMaxCaptionLength => aiMaxCaptionLength;
        public float AIPause => aiPause;
        public int AITimeout => aiTimeout;
        public bool LogAICaptions => logAICaptions;
        public bool AIContinueOnEmpty => aiContinueOnEmpty;
    }
}