using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Brain
{
    [Serializable]
    internal class OllamaVersionResponse
    {
        [JsonProperty("version")]
        public string Version { get; set; }
    }

    [Serializable]
    internal class OllamaModelListResponse
    {
        [JsonProperty("models")]
        public List<OllamaModelEntry> Models { get; set; }
    }

    [Serializable]
    internal class OllamaModelEntry
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("model")]
        public string Model { get; set; }

        [JsonProperty("modified_at")]
        public DateTime? ModifiedAt { get; set; }

        [JsonProperty("size")]
        public long Size { get; set; }

        [JsonProperty("digest")]
        public string Digest { get; set; }

        [JsonProperty("details")]
        public OllamaModelDetails Details { get; set; }
    }

    [Serializable]
    internal class OllamaModelDetails
    {
        [JsonProperty("family")]
        public string Family { get; set; }

        [JsonProperty("families")]
        public List<string> Families { get; set; }

        [JsonProperty("parameter_size")]
        public string ParameterSize { get; set; }

        [JsonProperty("quantization_level")]
        public string QuantizationLevel { get; set; }
    }

    [Serializable]
    internal class OllamaPullStatus
    {
        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("digest")]
        public string Digest { get; set; }

        [JsonProperty("total")]
        public long Total { get; set; }

        [JsonProperty("completed")]
        public long Completed { get; set; }

        [JsonProperty("error")]
        public string Error { get; set; }
    }

    [Serializable]
    internal class OllamaGenerateRequest
    {
        [JsonProperty("model")]
        public string Model { get; set; }

        [JsonProperty("prompt")]
        public string Prompt { get; set; }

        [JsonProperty("images", NullValueHandling = NullValueHandling.Ignore)]
        public string[] Images { get; set; }

        [JsonProperty("stream")]
        public bool Stream { get; set; }

        [JsonProperty("think")]
        public bool Think { get; set; }

        [JsonProperty("keep_alive", NullValueHandling = NullValueHandling.Ignore)]
        public string KeepAlive { get; set; }

        [JsonProperty("options", NullValueHandling = NullValueHandling.Ignore)]
        public OllamaRequestOptions Options { get; set; }
    }

    [Serializable]
    internal class OllamaRequestOptions
    {
        [JsonProperty("num_predict", NullValueHandling = NullValueHandling.Ignore)]
        public int? NumPredict { get; set; }

        [JsonProperty("temperature", NullValueHandling = NullValueHandling.Ignore)]
        public float? Temperature { get; set; }

        [JsonProperty("stop", NullValueHandling = NullValueHandling.Ignore)]
        public string[] Stop { get; set; }
    }

    [Serializable]
    internal class OllamaGenerateResponse
    {
        [JsonProperty("response")]
        public string Response { get; set; }

        [JsonProperty("done")]
        public bool Done { get; set; }

        [JsonProperty("total_duration")]
        public long TotalDuration { get; set; }

        [JsonProperty("load_duration")]
        public long LoadDuration { get; set; }

        [JsonProperty("prompt_eval_count")]
        public int PromptEvalCount { get; set; }

        [JsonProperty("prompt_eval_duration")]
        public long PromptEvalDuration { get; set; }

        [JsonProperty("eval_count")]
        public int EvalCount { get; set; }

        [JsonProperty("eval_duration")]
        public long EvalDuration { get; set; }
    }

    [Serializable]
    internal class OllamaChatRequest
    {
        [JsonProperty("model")]
        public string Model { get; set; }

        [JsonProperty("messages")]
        public List<OllamaChatMessage> Messages { get; set; }

        [JsonProperty("stream")]
        public bool Stream { get; set; }
    }

    [Serializable]
    internal class OllamaChatMessage
    {
        [JsonProperty("role")]
        public string Role { get; set; }

        [JsonProperty("content")]
        public string Content { get; set; }
    }

    [Serializable]
    internal class OllamaChatResponse
    {
        [JsonProperty("message")]
        public OllamaChatMessage Message { get; set; }

        [JsonProperty("done")]
        public bool Done { get; set; }

        [JsonProperty("total_duration")]
        public long TotalDuration { get; set; }

        [JsonProperty("load_duration")]
        public long LoadDuration { get; set; }

        [JsonProperty("prompt_eval_count")]
        public int PromptEvalCount { get; set; }

        [JsonProperty("prompt_eval_duration")]
        public long PromptEvalDuration { get; set; }

        [JsonProperty("eval_count")]
        public int EvalCount { get; set; }

        [JsonProperty("eval_duration")]
        public long EvalDuration { get; set; }
    }

    [Serializable]
    internal class OllamaDeleteRequest
    {
        [JsonProperty("model")]
        public string Model { get; set; }
    }
}
