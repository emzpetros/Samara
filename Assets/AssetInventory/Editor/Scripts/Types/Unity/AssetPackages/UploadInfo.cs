using System;

namespace AssetInventory
{
    [Serializable]
    public sealed class UploadInfo
    {
        public string assetCount;
        public string downloadSize;
        public string downloadS3key;
        public string uploadS3key;
        public string versionNumber;
        public string[] srps;
        public string[] dependencies;

        public override string ToString()
        {
            return $"Upload Info ({downloadSize} bytes, {assetCount} files)";
        }
    }
}
