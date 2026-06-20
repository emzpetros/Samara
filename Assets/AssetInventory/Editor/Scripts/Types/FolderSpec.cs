using System;

namespace AssetInventory
{
    [Serializable]
    public sealed class FolderSpec
    {
        public int folderType; // 0 = packages, 1 = media, 2 = zip
        public int scanFor; // 0 = all media, 1 = all, 3 = audio, 4 = images, 5 = models, 7 = pattern
        public bool enabled = true;
        public bool assignTag;
        public string tag;
        public bool storeRelative;
        public string relativeKey;
        public string location;
        public string pattern;
        public string excludedExtensions;
        public string excludedDirectories;
        public bool createPreviews = true;
        public bool removeOrphans = true;
        public bool attachToPackage = true;
        public int packageMode = 0; // 0 = Root Folder, 1 = First Level, 2 = Second Level
        public bool detectUnityProjects = true;
        public bool checkSize;

        public FolderSpec()
        {
        }

        public FolderSpec(string location)
        {
            this.location = location;
        }

        public FolderSpec(FolderSpec other)
        {
            if (other == null) return;

            folderType = other.folderType;
            scanFor = other.scanFor;
            enabled = other.enabled;
            assignTag = other.assignTag;
            tag = other.tag;
            storeRelative = other.storeRelative;
            relativeKey = other.relativeKey;
            location = other.location;
            pattern = other.pattern;
            excludedExtensions = other.excludedExtensions;
            excludedDirectories = other.excludedDirectories;
            createPreviews = other.createPreviews;
            removeOrphans = other.removeOrphans;
            attachToPackage = other.attachToPackage;
            packageMode = other.packageMode;
            detectUnityProjects = other.detectUnityProjects;
            checkSize = other.checkSize;
        }

        public string GetLocation(bool expanded)
        {
            return expanded ? Paths.DeRel(location) : location;
        }

        public override string ToString()
        {
            return $"Folder Spec '{location}' ({folderType}, {enabled})";
        }
    }
}
