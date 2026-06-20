using System;
using System.Collections.Generic;
using System.IO;

namespace AssetInventory
{
    public static class ProjectMetadataCache
    {
        public struct Metadata
        {
            public long Size;
            public int Width;
            public int Height;
            public float Length;
            public int VertexCount;
            public long FileTimestamp;

            public bool HasSize => Size >= 0;
            public bool HasDimensions => Width >= 0;
            public bool HasLength => Length >= 0;
            public bool HasVertexCount => VertexCount >= 0;
        }

        private static readonly Dictionary<string, Metadata> _cache = new Dictionary<string, Metadata>();

        public static bool TryGet(string guid, string fullPath, out Metadata metadata)
        {
            if (_cache.TryGetValue(guid, out metadata))
            {
                long currentTimestamp = GetFileTimestamp(fullPath);
                if (currentTimestamp == metadata.FileTimestamp) return true;

                _cache.Remove(guid);
            }
            metadata = CreateEmpty(fullPath);
            return false;
        }

        public static void Store(string guid, Metadata metadata)
        {
            _cache[guid] = metadata;
        }

        public static void Invalidate(IEnumerable<string> guids)
        {
            if (guids == null) return;
            foreach (string guid in guids)
            {
                _cache.Remove(guid);
            }
        }

        public static void Clear()
        {
            _cache.Clear();
        }

        public static Metadata CreateEmpty(string fullPath)
        {
            return new Metadata
            {
                Size = -1,
                Width = -1,
                Height = -1,
                Length = -1,
                VertexCount = -1,
                FileTimestamp = GetFileTimestamp(fullPath)
            };
        }

        private static long GetFileTimestamp(string path)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path).Ticks;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
