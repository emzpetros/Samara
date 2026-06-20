using Automator;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetInventory
{
    [Serializable]
    public sealed class UpdateAction
    {
        public enum Phase
        {
            Independent,
            Pre,
            Index,
            Post
        }

        public string key;
        public string name;
        public string description;
        public Phase phase = Phase.Independent;
        public bool supportsForce;
        public bool nonBlocking;
        public bool allowParallel;
        public bool hidden;

        // runtime
        public bool scheduled;
        [NonSerialized] public List<ActionProgress> progress = new List<ActionProgress>();

        public UpdateAction()
        {
        }

        public bool IsRunning()
        {
            foreach (ActionProgress p in progress)
            {
                if (p.IsRunning()) return true;
            }
            return false;
        }

        public void MarkStarted()
        {
            scheduled = false;
        }

        public void CheckStopped()
        {
            foreach (ActionProgress p in progress)
            {
                if (p.IsRunning()) return;
            }

            progress.Clear();
        }

        public override string ToString()
        {
            return $"Update Action '{name}' ({key})";
        }
    }
}