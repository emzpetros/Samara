using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.VFX;

namespace AssetInventory
{
    /// <summary>
    /// Centralized bounds calculation for all preview types.
    /// Consolidates bounds logic from PreviewManager and CustomPrefabPreviewGenerator.
    /// </summary>
    public static class PreviewBoundsCalculator
    {
        /// <summary>
        /// Get bounds based on prefab type, handling static and animated content
        /// </summary>
        public static Bounds GetBoundsOverTime(Renderer[] renderers, CustomPrefabPreviewGenerator.PrefabType prefabType, float animationDuration, GameObject prefab = null)
        {
            if (prefabType == CustomPrefabPreviewGenerator.PrefabType.Particles && prefab != null && animationDuration > 0f)
            {
                return GetParticleBoundsOverTime(prefab, animationDuration, 5);
            }
            else if (prefabType == CustomPrefabPreviewGenerator.PrefabType.VFX && prefab != null && animationDuration > 0f)
            {
                return GetVFXBoundsOverTime(prefab, animationDuration);
            }
            else
            {
                return GetGlobalBounds(renderers, prefabType, prefab);
            }
        }

        /// <summary>
        /// Get global bounds from renderers (works for scenes, prefabs, and static objects)
        /// </summary>
        public static Bounds GetGlobalBounds(Renderer[] renderers, CustomPrefabPreviewGenerator.PrefabType prefabType = CustomPrefabPreviewGenerator.PrefabType.Model, GameObject prefabObject = null)
        {
            // For particle systems, use actual particle position sampling for accurate bounds
            if (prefabType == CustomPrefabPreviewGenerator.PrefabType.Particles && prefabObject != null)
            {
                return GetCurrentParticleBounds(prefabObject);
            }

            // For FBX models, use tighter mesh-based bounds instead of renderer bounds
            if (prefabType == CustomPrefabPreviewGenerator.PrefabType.FBX)
            {
                return GetFBXMeshBounds(renderers);
            }

            // For Model type prefabs, exclude particle/VFX renderers from bounds calculation.
            // This prevents decorative effects (dust, smoke, light particles) from affecting
            // camera framing, and avoids "animating" bounds as particles move.
            Renderer[] renderersToUse = renderers;
            if (prefabType == CustomPrefabPreviewGenerator.PrefabType.Model)
            {
                List<Renderer> staticRenderers = new List<Renderer>();
                foreach (Renderer r in renderers)
                {
                    // Skip ParticleSystemRenderer - their bounds change as particles move
                    if (r is ParticleSystemRenderer) continue;
                    // Skip VFXRenderer - same issue with dynamic bounds
                    if (r.GetType().Name == "VFXRenderer") continue;
                    staticRenderers.Add(r);
                }
                // Only use filtered list if we still have renderers; otherwise fall back to all
                if (staticRenderers.Count > 0)
                {
                    renderersToUse = staticRenderers.ToArray();
                }
            }

            // For other types, use standard renderer bounds
            if (renderersToUse.Length == 0) return new Bounds(Vector3.zero, Vector3.one);

            Vector3 center = Vector3.zero;
            foreach (Renderer renderer in renderersToUse)
            {
                center += renderer.bounds.center;
            }
            center /= renderersToUse.Length;

            Bounds globalBounds = new Bounds(center, Vector3.zero);
            foreach (Renderer renderer in renderersToUse)
            {
                globalBounds.Encapsulate(renderer.bounds);
            }

            return globalBounds;
        }

        /// <summary>
        /// Get current particle bounds based on live particle positions
        /// </summary>
        public static Bounds GetCurrentParticleBounds(GameObject go)
        {
            ParticleSystem[] particleSystems = go.GetComponentsInChildren<ParticleSystem>();
            if (particleSystems.Length == 0)
            {
                // Fallback to renderer bounds if no particle systems found
                Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
                return GetGlobalBounds(renderers);
            }

            bool hasAnyParticles = false;
            Vector3 minBounds = Vector3.positiveInfinity;
            Vector3 maxBounds = Vector3.negativeInfinity;

            // Collect all particle positions and sizes for distribution analysis
            List<Vector3> allParticlePositions = new List<Vector3>();
            List<float> allParticleRadii = new List<float>();
            Dictionary<string, int> particlesPerSystem = new Dictionary<string, int>();

            foreach (ParticleSystem ps in particleSystems)
            {
                // Skip particle systems whose renderer is disabled — their particles are invisible
                // and should not influence camera framing or bounds calculation.
                ParticleSystemRenderer boundsPSR = ps.GetComponent<ParticleSystemRenderer>();
                if (boundsPSR != null && !boundsPSR.enabled) continue;

                // Get all alive particles
                ParticleSystem.Particle[] particles = new ParticleSystem.Particle[ps.particleCount];
                int aliveCount = ps.GetParticles(particles);

                if (aliveCount == 0) continue;

                hasAnyParticles = true;

                // Determine if we need to transform particles to world space
                ParticleSystem.MainModule main = ps.main;
                bool useWorldSpace = main.simulationSpace == ParticleSystemSimulationSpace.World;
                Transform psTransform = ps.transform;

                ParticleSystemRenderer psRenderer = ps.GetComponent<ParticleSystemRenderer>();

                // Track min/max particle positions and sizes for analysis
                Vector3 minParticlePos = Vector3.positiveInfinity;
                Vector3 maxParticlePos = Vector3.negativeInfinity;
                float minParticleSize = float.MaxValue;
                float maxParticleSize = float.MinValue;
                int particlesIncluded = 0;

                // Collect particle positions for distribution analysis
                // All alive particles are included — alpha is handled via radius weighting
                // rather than hard exclusion, because particles using additive blending or
                // SizeOverLifetime can be visually prominent even at low alpha values.
                for (int i = 0; i < aliveCount; i++)
                {
                    Vector3 particlePos = particles[i].position;

                    // Transform to world space if particles are in local space
                    if (!useWorldSpace)
                    {
                        particlePos = psTransform.TransformPoint(particlePos);
                    }

                    particlesIncluded++;
                    float rawParticleSize = particles[i].GetCurrentSize(ps);
                    float particleRadius = rawParticleSize * 0.5f; // Use half size as radius

                    // Account for ParticleSystemRenderer visual scale
                    if (psRenderer != null)
                    {
                        Vector3 rendererScale = psRenderer.transform.lossyScale;
                        // Apply renderer scale to particle radius for accurate visual bounds
                        particleRadius *= Mathf.Max(rendererScale.x, rendererScale.y, rendererScale.z);

                        // Account for stretched billboard visual extent: particles are elongated
                        // along their velocity direction by lengthScale and velocityScale
                        if (psRenderer.renderMode == ParticleSystemRenderMode.Stretch)
                        {
                            float stretchedHalfLength = (rawParticleSize * psRenderer.lengthScale
                                + particles[i].velocity.magnitude * psRenderer.velocityScale) * 0.5f;
                            particleRadius = Mathf.Max(particleRadius, stretchedHalfLength);
                        }
                    }

                    // Weight radius by alpha - transparent particles (glows/shines) should contribute less to bounds expansion
                    // This prevents large semi-transparent glow effects from inflating bounds excessively
                    Color32 currentColor = particles[i].GetCurrentColor(ps);
                    float currentAlpha = currentColor.a / 255f;
                    // Use sqrt of alpha with a floor of 0.25 so that even near-zero alpha particles
                    // still contribute meaningfully. Effects like CFX_Virus grow particles via SizeOverLifetime
                    // while fading alpha — without a floor their largest state would contribute zero radius.
                    // Additive-blend particles also appear bright on dark backgrounds regardless of alpha.
                    float alphaWeight = Mathf.Max(0.25f, Mathf.Sqrt(currentAlpha));
                    float weightedRadius = particleRadius * alphaWeight;

                    // Track min/max for analysis
                    minParticlePos = Vector3.Min(minParticlePos, particlePos);
                    maxParticlePos = Vector3.Max(maxParticlePos, particlePos);
                    minParticleSize = Mathf.Min(minParticleSize, rawParticleSize);
                    maxParticleSize = Mathf.Max(maxParticleSize, rawParticleSize);

                    // Collect for distribution analysis - use alpha-weighted radius
                    allParticlePositions.Add(particlePos);
                    allParticleRadii.Add(weightedRadius);
                }

                particlesPerSystem[ps.name] = particlesIncluded;
            }

            // Calculate bounds using percentile-based approach to trim outliers (only when beneficial)
            if (allParticlePositions.Count > 0)
            {
                // Calculate percentiles for each axis to trim sparse outliers
                // Use 5th to 95th percentile to exclude extreme outliers while keeping main concentration
                const float PERCENTILE_LOW = 0.05f;
                const float PERCENTILE_HIGH = 0.95f;

                // Sort positions by each axis
                System.Collections.Generic.List<float> xCoords = new System.Collections.Generic.List<float>();
                System.Collections.Generic.List<float> yCoords = new System.Collections.Generic.List<float>();
                System.Collections.Generic.List<float> zCoords = new System.Collections.Generic.List<float>();

                for (int i = 0; i < allParticlePositions.Count; i++)
                {
                    xCoords.Add(allParticlePositions[i].x);
                    yCoords.Add(allParticlePositions[i].y);
                    zCoords.Add(allParticlePositions[i].z);
                }

                xCoords.Sort();
                yCoords.Sort();
                zCoords.Sort();

                // Calculate full span (min to max) for comparison
                Vector3 fullSpan = new Vector3(xCoords[xCoords.Count - 1] - xCoords[0], yCoords[yCoords.Count - 1] - yCoords[0], zCoords[zCoords.Count - 1] - zCoords[0]);

                // Calculate percentile indices
                int lowIndex = Mathf.Max(0, Mathf.FloorToInt(allParticlePositions.Count * PERCENTILE_LOW));
                int highIndex = Mathf.Min(allParticlePositions.Count - 1, Mathf.CeilToInt(allParticlePositions.Count * PERCENTILE_HIGH));

                // Get percentile bounds
                float xMin = xCoords[lowIndex];
                float xMax = xCoords[highIndex];
                float yMin = yCoords[lowIndex];
                float yMax = yCoords[highIndex];
                float zMin = zCoords[lowIndex];
                float zMax = zCoords[highIndex];

                // Calculate percentile span
                Vector3 percentileSpan = new Vector3(xMax - xMin, yMax - yMin, zMax - zMin);

                // Determine if percentile trimming is beneficial (only trim if significant outliers exist)
                // For uniform distributions (like rotating torus), trimming would remove important content
                // Only apply percentile trimming if it removes > 15% of the span on any axis
                bool shouldUsePercentile = false;
                if (fullSpan.x > 0.001f && (fullSpan.x - percentileSpan.x) / fullSpan.x > 0.15f) shouldUsePercentile = true;
                if (fullSpan.y > 0.001f && (fullSpan.y - percentileSpan.y) / fullSpan.y > 0.15f) shouldUsePercentile = true;
                if (fullSpan.z > 0.001f && (fullSpan.z - percentileSpan.z) / fullSpan.z > 0.15f) shouldUsePercentile = true;


                // Calculate max radius (used in both paths)
                float maxRadius = 0f;
                if (shouldUsePercentile)
                {
                    // Use percentile-based bounds (there are clear outliers)
                    // Use percentile of particle radii instead of maximum to avoid inflation from large particles
                    System.Collections.Generic.List<float> radiiInBounds = new System.Collections.Generic.List<float>();
                    for (int i = 0; i < allParticlePositions.Count; i++)
                    {
                        Vector3 pos = allParticlePositions[i];
                        // Check if particle is within percentile bounds
                        if (pos.x >= xMin && pos.x <= xMax &&
                            pos.y >= yMin && pos.y <= yMax &&
                            pos.z >= zMin && pos.z <= zMax)
                        {
                            radiiInBounds.Add(allParticleRadii[i]);
                        }
                    }

                    if (radiiInBounds.Count > 0)
                    {
                        radiiInBounds.Sort();
                        // Use 90th percentile of radii within bounds to avoid extreme outliers
                        int radiusPercentileIndex = Mathf.Min(radiiInBounds.Count - 1, Mathf.CeilToInt(radiiInBounds.Count * 0.90f));
                        maxRadius = radiiInBounds[radiusPercentileIndex];
                    }
                    else if (allParticleRadii.Count > 0)
                    {
                        // Fallback: use 90th percentile of all radii if no particles in bounds
                        List<float> sortedRadii = new List<float>(allParticleRadii);
                        sortedRadii.Sort();
                        int radiusPercentileIndex = Mathf.Min(sortedRadii.Count - 1, Mathf.CeilToInt(sortedRadii.Count * 0.90f));
                        maxRadius = sortedRadii[radiusPercentileIndex];
                    }

                    // Cap expansion to prevent particle radius from inflating bounds excessively
                    // Maximum expansion per axis is 30% of the percentile span for that axis
                    float maxExpansionX = Mathf.Min(maxRadius, percentileSpan.x * 0.3f);
                    float maxExpansionY = Mathf.Min(maxRadius, percentileSpan.y * 0.3f);
                    float maxExpansionZ = Mathf.Min(maxRadius, percentileSpan.z * 0.3f);

                    // Expand percentile bounds by capped particle radius
                    minBounds = new Vector3(xMin - maxExpansionX, yMin - maxExpansionY, zMin - maxExpansionZ);
                    maxBounds = new Vector3(xMax + maxExpansionX, yMax + maxExpansionY, zMax + maxExpansionZ);
                }
                else
                {
                    // Use full bounds (uniform distribution, no significant outliers)
                    // For uniform distributions, use larger expansion to ensure all particles are visible
                    // Calculate max radius from all particles for expansion
                    float actualMaxRadius = maxRadius;
                    if (allParticleRadii.Count > 0)
                    {
                        List<float> sortedRadii = new List<float>(allParticleRadii);
                        sortedRadii.Sort();
                        // Use 90th percentile of all radii for normal expansion
                        int radiusPercentileIndex = Mathf.Min(sortedRadii.Count - 1, Mathf.CeilToInt(sortedRadii.Count * 0.90f));
                        maxRadius = sortedRadii[radiusPercentileIndex];
                        // Keep track of the true maximum radius for the zero-span case
                        actualMaxRadius = sortedRadii[sortedRadii.Count - 1];
                    }

                    // For uniform distributions, cap expansion to prevent large particles from inflating bounds excessively
                    // Previously used 0.75 for billboard which was too aggressive for glow/shine effects
                    // 0.35 provides tight framing while still showing particle content
                    // Note: effects with small particles (radius < span*0.35) won't be affected as radius is the limit
                    float expansionFactor = 0.35f;
                    // If fullSpan is ~0 (all particles at same position), use the actual maximum radius
                    // (not P90) so that large stationary particles (e.g. CFX_Virus Bubble2 growing to 1.7
                    // world units) aren't drowned out by many small co-located root particles.
                    // When everything is at the same spot, outlier removal via P90 is counterproductive
                    // because the largest particle defines the true visual extent.
                    float zeroSpanRadius = actualMaxRadius;
                    float maxExpansionX = fullSpan.x > 0.001f ? Mathf.Min(maxRadius, fullSpan.x * expansionFactor) : zeroSpanRadius;
                    float maxExpansionY = fullSpan.y > 0.001f ? Mathf.Min(maxRadius, fullSpan.y * expansionFactor) : zeroSpanRadius;
                    float maxExpansionZ = fullSpan.z > 0.001f ? Mathf.Min(maxRadius, fullSpan.z * expansionFactor) : zeroSpanRadius;

                    // Use full bounds with capped expansion
                    minBounds = new Vector3(xCoords[0] - maxExpansionX, yCoords[0] - maxExpansionY, zCoords[0] - maxExpansionZ);
                    maxBounds = new Vector3(xCoords[xCoords.Count - 1] + maxExpansionX, yCoords[yCoords.Count - 1] + maxExpansionY, zCoords[zCoords.Count - 1] + maxExpansionZ);
                }
            }

            // Include non-particle renderers (MeshRenderer, SkinnedMeshRenderer, etc.) in bounds
            // Includes accompanying models (e.g., torch geometry with fire particles)
            // But filter out disproportionately large renderers (e.g., ground-plane decals)
            // that would dominate particle bounds and push the camera too far back.
            Renderer[] allRenderers = go.GetComponentsInChildren<Renderer>();

            // First, collect particle bounds span to compare against non-particle renderers
            Vector3 particleSpan = (!float.IsInfinity(minBounds.x) && !float.IsInfinity(maxBounds.x))
                ? (maxBounds - minBounds) : Vector3.zero;
            float particleMaxSpan = Mathf.Max(particleSpan.x, particleSpan.y, particleSpan.z);

            foreach (Renderer renderer in allRenderers)
            {
                if (renderer is ParticleSystemRenderer)
                {
                    // For ParticleSystemRenderer, Unity's calculated bounds are unreliable for billboarded particles
                    // They can be inflated by billboarding effects, shader effects, or incorrect bounds calculation.
                    // Since we already calculated accurate bounds from actual particle positions and sizes above,
                    // we should NOT use ParticleSystemRenderer bounds - they would only add inflation.
                    // Skip ParticleSystemRenderer bounds entirely and rely on calculated particle bounds.
                    hasAnyParticles = true;
                    continue; // Skip ParticleSystemRenderer bounds - use calculated particle bounds instead
                }

                // Skip disproportionately large non-particle renderers (e.g., ground-plane decals with
                // scale 25x2x25) that would dominate particle bounds and ruin camera framing.
                // Only filter when we actually have particle bounds to compare against.
                if (particleMaxSpan > 0.001f)
                {
                    Vector3 rendererSize = renderer.bounds.size;
                    float rendererMaxSpan = Mathf.Max(rendererSize.x, rendererSize.y, rendererSize.z);
                    if (rendererMaxSpan > particleMaxSpan * 5f)
                    {
                        continue; // Skip this oversized renderer — it would dominate particle framing
                    }
                }

                // Include this renderer's bounds
                minBounds = Vector3.Min(minBounds, renderer.bounds.min);
                maxBounds = Vector3.Max(maxBounds, renderer.bounds.max);
                hasAnyParticles = true; // Treat as having content
            }

            // Check if bounds are still at infinity (no actual content was calculated)
            bool boundsAreValid = !float.IsInfinity(minBounds.x) && !float.IsInfinity(maxBounds.x);

            // If no particles are alive and no non-particle renderers, OR bounds are invalid, fall back to renderer bounds
            if (!hasAnyParticles || !boundsAreValid)
            {
                Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
                return GetGlobalBounds(renderers);
            }

            // Calculate center and size from min/max
            Vector3 center = (minBounds + maxBounds) / 2f;
            Vector3 size = maxBounds - minBounds;

            // Apply minimal padding to prevent edge clipping (2% padding)
            float paddingFactor = 1.02f;
            size *= paddingFactor;

            // Ensure minimum size to prevent division by zero or extreme zoom
            float minSize = 0.1f;
            size.x = Mathf.Max(size.x, minSize);
            size.y = Mathf.Max(size.y, minSize);
            size.z = Mathf.Max(size.z, minSize);

            return new Bounds(center, size);
        }

        /// <summary>
        /// Get particle bounds over time by simulating at multiple time points
        /// </summary>
        public static Bounds GetParticleBoundsOverTime(GameObject go, float duration, int sampleCount = 10)
        {
            // Sample bounds over time to frame entire motion, not just initial position
            // Increased from 5 to 10 samples for better coverage of delayed bursts and explosions

            ParticleSystem[] particleSystems = go.GetComponentsInChildren<ParticleSystem>();
            if (particleSystems.Length == 0) return new Bounds(Vector3.zero, Vector3.one);

            Vector3 minBounds = Vector3.one * float.MaxValue;
            Vector3 maxBounds = Vector3.one * float.MinValue;
            bool hasAnyParticles = false;

            // Track particle counts at each time for weighted bounds calculation
            List<int> particleCountsPerSample = new List<int>();
            List<Bounds> boundsPerSample = new List<Bounds>();

            // Check if any particle system uses billboard rendering (may need special handling)
            bool hasBillboardRenderer = false;
            foreach (ParticleSystem ps in particleSystems)
            {
                ParticleSystemRenderer psRenderer = ps.GetComponent<ParticleSystemRenderer>();
                if (psRenderer != null && (
                        psRenderer.renderMode == ParticleSystemRenderMode.Billboard ||
                        psRenderer.renderMode == ParticleSystemRenderMode.Stretch ||
                        psRenderer.renderMode == ParticleSystemRenderMode.HorizontalBillboard ||
                        psRenderer.renderMode == ParticleSystemRenderMode.VerticalBillboard))
                {
                    hasBillboardRenderer = true;
                    break;
                }
            }
            // Suppress unused variable warning — kept for future use
            _ = hasBillboardRenderer;

            // Sample particle positions at multiple points in time
            // Use non-uniform sampling to focus more on latter portion where explosions occur
            for (int sample = 0; sample < sampleCount; sample++)
            {
                float time;
                if (sampleCount > 1)
                {
                    // Non-uniform sampling: use squared distribution to focus more samples on latter portion
                    // For fireworks/explosions, the interesting part often happens in the 50-80% range
                    float t = sample / (float)(sampleCount - 1);
                    // Square the distribution to get more samples toward the end
                    float biasedT = t * t * 0.5f + t * 0.5f; // Mix of linear and squared for balanced coverage
                    time = duration * biasedT;
                }
                else
                {
                    time = duration * 0.5f;
                }

                // Simulate all root-level particle systems. For typical prefabs where
                // particleSystems[0] is the common ancestor, this is identical to before.
                // For branched hierarchies (sibling PS with no common PS ancestor), each
                // branch root is simulated independently so all particles appear.
                List<ParticleSystem> rootPSList = FindRootParticleSystems(particleSystems, go);
                if (rootPSList.Count > 0)
                {
                    // Stop and clear every system individually to ensure none are still
                    // playing when we set the random seed (Unity forbids seeding active systems)
                    foreach (ParticleSystem ps in particleSystems)
                    {
                        ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                        ps.Clear(false);
                    }

                    foreach (ParticleSystem ps in particleSystems)
                    {
                        ps.useAutoRandomSeed = false;
                        ps.randomSeed = AI.Config.cpParticleSeed;
                    }

                    ParticleSystem.MainModule rootMain = rootPSList[0].main;
                    float realTime = rootMain.simulationSpeed > 0f ? time / rootMain.simulationSpeed : time;
                    foreach (ParticleSystem root in rootPSList)
                    {
                        root.Simulate(realTime, true, true, true);
                        root.Play(true);
                    }
                }

                // Count and track particles at this time point
                int totalParticlesAtTime = 0;
                bool hasSampleParticles = false;

                // Collect all particle positions and radii for percentile-based calculation
                List<Vector3> sampleParticlePositions = new List<Vector3>();
                List<float> sampleParticleRadii = new List<float>();

                // Sample particle positions at this time
                foreach (ParticleSystem ps in particleSystems)
                {
                    // Skip disabled renderers — invisible particles shouldn't affect bounds
                    ParticleSystemRenderer samplePSR = ps.GetComponent<ParticleSystemRenderer>();
                    if (samplePSR != null && !samplePSR.enabled) continue;

                    int aliveCount = ps.particleCount;
                    if (aliveCount == 0) continue;

                    totalParticlesAtTime += aliveCount;
                    hasAnyParticles = true;
                    hasSampleParticles = true;

                    ParticleSystem.Particle[] particles = new ParticleSystem.Particle[aliveCount];
                    ps.GetParticles(particles, aliveCount);

                    ParticleSystem.MainModule main = ps.main;
                    bool useWorldSpace = main.simulationSpace == ParticleSystemSimulationSpace.World;
                    Transform psTransform = ps.transform;

                    for (int i = 0; i < aliveCount; i++)
                    {
                        Vector3 particlePos = particles[i].position;
                        if (!useWorldSpace)
                        {
                            particlePos = psTransform.TransformPoint(particlePos);
                        }

                        float rawParticleSize = particles[i].GetCurrentSize(ps);
                        float particleRadius = rawParticleSize * 0.5f;

                        // Account for ParticleSystemRenderer visual scale
                        ParticleSystemRenderer psRenderer = ps.GetComponent<ParticleSystemRenderer>();
                        if (psRenderer != null)
                        {
                            Vector3 rendererScale = psRenderer.transform.lossyScale;
                            // Apply renderer scale to particle radius for accurate visual bounds
                            particleRadius *= Mathf.Max(rendererScale.x, rendererScale.y, rendererScale.z);

                            // Account for stretched billboard visual extent: particles are elongated
                            // along their velocity direction by lengthScale and velocityScale
                            if (psRenderer.renderMode == ParticleSystemRenderMode.Stretch)
                            {
                                float stretchedHalfLength = (rawParticleSize * psRenderer.lengthScale
                                    + particles[i].velocity.magnitude * psRenderer.velocityScale) * 0.5f;
                                particleRadius = Mathf.Max(particleRadius, stretchedHalfLength);
                            }
                        }

                        // Weight radius by alpha - transparent particles (glows/shines) should contribute less
                        // This matches the same weighting used in GetCurrentParticleBounds for consistent
                        // framing between static and animated previews
                        Color32 currentColor = particles[i].GetCurrentColor(ps);
                        float currentAlpha = currentColor.a / 255f;
                        // Floor at 0.25 so near-zero alpha particles still contribute meaningfully
                        float alphaWeight = Mathf.Max(0.25f, Mathf.Sqrt(currentAlpha));
                        particleRadius *= alphaWeight;

                        // Collect for percentile-based calculation
                        sampleParticlePositions.Add(particlePos);
                        sampleParticleRadii.Add(particleRadius);
                    }
                }

                // Calculate bounds for this time sample (conditional percentile trimming)
                Vector3 sampleMinBounds = Vector3.one * float.MaxValue;
                Vector3 sampleMaxBounds = Vector3.one * float.MinValue;

                if (sampleParticlePositions.Count > 0)
                {
                    // Use percentile-based approach to trim outliers at each time sample (only when beneficial)
                    const float PERCENTILE_LOW = 0.05f;
                    const float PERCENTILE_HIGH = 0.95f;

                    // Sort positions by each axis
                    List<float> xCoords = new List<float>();
                    List<float> yCoords = new List<float>();
                    List<float> zCoords = new List<float>();

                    for (int i = 0; i < sampleParticlePositions.Count; i++)
                    {
                        xCoords.Add(sampleParticlePositions[i].x);
                        yCoords.Add(sampleParticlePositions[i].y);
                        zCoords.Add(sampleParticlePositions[i].z);
                    }

                    xCoords.Sort();
                    yCoords.Sort();
                    zCoords.Sort();

                    // Calculate full span for comparison
                    Vector3 fullSpan = new Vector3(xCoords[xCoords.Count - 1] - xCoords[0], yCoords[yCoords.Count - 1] - yCoords[0], zCoords[zCoords.Count - 1] - zCoords[0]);

                    // Calculate percentile indices
                    int lowIndex = Mathf.Max(0, Mathf.FloorToInt(sampleParticlePositions.Count * PERCENTILE_LOW));
                    int highIndex = Mathf.Min(sampleParticlePositions.Count - 1, Mathf.CeilToInt(sampleParticlePositions.Count * PERCENTILE_HIGH));

                    // Get percentile bounds
                    float xMin = xCoords[lowIndex];
                    float xMax = xCoords[highIndex];
                    float yMin = yCoords[lowIndex];
                    float yMax = yCoords[highIndex];
                    float zMin = zCoords[lowIndex];
                    float zMax = zCoords[highIndex];

                    // Calculate percentile span
                    Vector3 percentileSpan = new Vector3(xMax - xMin, yMax - yMin, zMax - zMin);

                    // Determine if percentile trimming is beneficial (only trim if significant outliers exist)
                    bool shouldUsePercentile = false;
                    if (fullSpan.x > 0.001f && (fullSpan.x - percentileSpan.x) / fullSpan.x > 0.15f) shouldUsePercentile = true;
                    if (fullSpan.y > 0.001f && (fullSpan.y - percentileSpan.y) / fullSpan.y > 0.15f) shouldUsePercentile = true;
                    if (fullSpan.z > 0.001f && (fullSpan.z - percentileSpan.z) / fullSpan.z > 0.15f) shouldUsePercentile = true;

                    float maxRadius = 0f;
                    if (shouldUsePercentile)
                    {
                        // Use percentile-based bounds (there are clear outliers)
                        // Use 90th percentile of particle radii from particles within percentile bounds
                        List<float> radiiInBounds = new List<float>();
                        for (int i = 0; i < sampleParticlePositions.Count; i++)
                        {
                            Vector3 pos = sampleParticlePositions[i];
                            if (pos.x >= xMin && pos.x <= xMax &&
                                pos.y >= yMin && pos.y <= yMax &&
                                pos.z >= zMin && pos.z <= zMax)
                            {
                                radiiInBounds.Add(sampleParticleRadii[i]);
                            }
                        }

                        if (radiiInBounds.Count > 0)
                        {
                            radiiInBounds.Sort();
                            int radiusPercentileIndex = Mathf.Min(radiiInBounds.Count - 1, Mathf.CeilToInt(radiiInBounds.Count * 0.90f));
                            maxRadius = radiiInBounds[radiusPercentileIndex];
                        }
                        else if (sampleParticleRadii.Count > 0)
                        {
                            List<float> sortedRadii = new List<float>(sampleParticleRadii);
                            sortedRadii.Sort();
                            int radiusPercentileIndex = Mathf.Min(sortedRadii.Count - 1, Mathf.CeilToInt(sortedRadii.Count * 0.90f));
                            maxRadius = sortedRadii[radiusPercentileIndex];
                        }

                        // Cap expansion to 30% of percentile span per axis
                        float maxExpansionX = Mathf.Min(maxRadius, percentileSpan.x * 0.3f);
                        float maxExpansionY = Mathf.Min(maxRadius, percentileSpan.y * 0.3f);
                        float maxExpansionZ = Mathf.Min(maxRadius, percentileSpan.z * 0.3f);

                        sampleMinBounds = new Vector3(xMin - maxExpansionX, yMin - maxExpansionY, zMin - maxExpansionZ);
                        sampleMaxBounds = new Vector3(xMax + maxExpansionX, yMax + maxExpansionY, zMax + maxExpansionZ);
                    }
                    else
                    {
                        // Use full bounds (uniform distribution, no significant outliers)
                        // For uniform distributions, use larger expansion to ensure all particles are visible
                        float actualMaxRadius = maxRadius;
                        if (sampleParticleRadii.Count > 0)
                        {
                            List<float> sortedRadii = new List<float>(sampleParticleRadii);
                            sortedRadii.Sort();
                            int radiusPercentileIndex = Mathf.Min(sortedRadii.Count - 1, Mathf.CeilToInt(sortedRadii.Count * 0.90f));
                            maxRadius = sortedRadii[radiusPercentileIndex];
                            // Keep track of the true maximum radius for the zero-span case
                            actualMaxRadius = sortedRadii[sortedRadii.Count - 1];
                        }

                        // Larger expansion for uniform distributions; billboard renderers need more since particles face camera
                        // Allow rotating torus and similar effects to show complete shape
                        // Unified to match GetCurrentParticleBounds (0.35) for consistent static/animated framing
                        float expansionFactor = 0.35f;
                        // If fullSpan is ~0 (all particles at same position), use the actual maximum radius
                        // (not P90) so that large stationary particles aren't drowned out by many small
                        // co-located particles. When everything is at the same spot, the largest particle
                        // defines the true visual extent.
                        float zeroSpanRadius = actualMaxRadius;
                        float maxExpansionX = fullSpan.x > 0.001f ? Mathf.Min(maxRadius, fullSpan.x * expansionFactor) : zeroSpanRadius;
                        float maxExpansionY = fullSpan.y > 0.001f ? Mathf.Min(maxRadius, fullSpan.y * expansionFactor) : zeroSpanRadius;
                        float maxExpansionZ = fullSpan.z > 0.001f ? Mathf.Min(maxRadius, fullSpan.z * expansionFactor) : zeroSpanRadius;

                        sampleMinBounds = new Vector3(xCoords[0] - maxExpansionX, yCoords[0] - maxExpansionY, zCoords[0] - maxExpansionZ);
                        sampleMaxBounds = new Vector3(xCoords[xCoords.Count - 1] + maxExpansionX, yCoords[yCoords.Count - 1] + maxExpansionY, zCoords[zCoords.Count - 1] + maxExpansionZ);
                    }
                }

                // Store sample data for weighted calculation
                particleCountsPerSample.Add(totalParticlesAtTime);
                if (hasSampleParticles)
                {
                    boundsPerSample.Add(new Bounds((sampleMinBounds + sampleMaxBounds) / 2f, sampleMaxBounds - sampleMinBounds));
                }
                else
                {
                    boundsPerSample.Add(new Bounds(Vector3.zero, Vector3.zero));
                }
            }

            // Weight bounds calculation toward times with more particles (explosion phase)
            // Ensures fireworks explosions are properly framed even if launch phase has fewer particles
            int maxParticleCount = 0;
            foreach (int count in particleCountsPerSample)
            {
                if (count > maxParticleCount) maxParticleCount = count;
            }

            // Use weighted bounds: times with more particles contribute more to final bounds
            // Low-weight samples (few particles, e.g., single rocket in launch phase) have their bounds
            // shrunk toward the weighted center to prevent sparse phases from dominating the union.
            // This is critical for effects like fireworks where the launch has 1 particle spanning a
            // large vertical range, but the explosion phase has hundreds in a concentrated area.

            // First pass: compute the weighted center from all samples
            Vector3 weightedCenterAccum = Vector3.zero;
            float totalWeight = 0f;
            for (int i = 0; i < boundsPerSample.Count; i++)
            {
                if (particleCountsPerSample[i] == 0) continue;
                float w = maxParticleCount > 0 ? (float)particleCountsPerSample[i] / maxParticleCount : 1f;
                w = Mathf.Max(w, 0.2f);
                weightedCenterAccum += boundsPerSample[i].center * w;
                totalWeight += w;
            }
            Vector3 weightedCenter = totalWeight > 0f ? weightedCenterAccum / totalWeight : Vector3.zero;

            // Second pass: shrink low-weight samples toward the weighted center before union
            for (int i = 0; i < boundsPerSample.Count; i++)
            {
                if (particleCountsPerSample[i] == 0) continue;

                // Weight based on particle count at this time (relative to peak)
                float weight = maxParticleCount > 0 ? (float)particleCountsPerSample[i] / maxParticleCount : 1f;

                // Apply at least 20% weight to all non-empty samples to avoid ignoring early phases
                weight = Mathf.Max(weight, 0.2f);

                Bounds sampleBounds = boundsPerSample[i];

                // For low-weight samples, shrink bounds toward the weighted center
                // This prevents a single rocket particle at launch from inflating the entire bounds
                // weight 1.0 -> no shrinkage, weight 0.2 -> shrink 60% toward center
                float shrinkFactor = Mathf.Lerp(0.6f, 0f, (weight - 0.2f) / 0.8f);
                Vector3 shrunkMin = Vector3.Lerp(sampleBounds.min, weightedCenter, shrinkFactor);
                Vector3 shrunkMax = Vector3.Lerp(sampleBounds.max, weightedCenter, shrinkFactor);

                minBounds = Vector3.Min(minBounds, shrunkMin);
                maxBounds = Vector3.Max(maxBounds, shrunkMax);
            }

            // Include non-particle renderers (MeshRenderer, SkinnedMeshRenderer, etc.) in bounds
            // Includes accompanying models (e.g., torch geometry with fire particles)
            Renderer[] allRenderers = go.GetComponentsInChildren<Renderer>();
            foreach (Renderer renderer in allRenderers)
            {
                if (renderer is ParticleSystemRenderer)
                {
                    // For ParticleSystemRenderer, Unity's calculated bounds are unreliable for billboarded particles
                    // They can be inflated by billboarding effects, shader effects, or incorrect bounds calculation.
                    // Since we already calculated accurate bounds from actual particle positions and sizes above,
                    // we should NOT use ParticleSystemRenderer bounds - they would only add inflation.
                    // Skip ParticleSystemRenderer bounds entirely and rely on calculated particle bounds.
                    hasAnyParticles = true;
                    continue; // Skip ParticleSystemRenderer bounds - use calculated particle bounds instead
                }

                minBounds = Vector3.Min(minBounds, renderer.bounds.min);
                maxBounds = Vector3.Max(maxBounds, renderer.bounds.max);
                hasAnyParticles = true; // Treat as having content
            }

            if (!hasAnyParticles)
            {
                Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
                return GetGlobalBounds(renderers);
            }

            Vector3 center = (minBounds + maxBounds) / 2f;
            Vector3 size = maxBounds - minBounds;

            // Cap extreme aspect ratios to prevent the preview from becoming a tiny sliver
            // For effects like fireworks with extreme vertical range but narrow horizontal spread,
            // the preview would be dominated by empty space. Cap any axis to 4x the smallest axis.
            float minAxis = Mathf.Max(Mathf.Min(size.x, size.y, size.z), 0.01f);
            float maxAllowedRatio = 4f;
            float maxAllowedSize = minAxis * maxAllowedRatio;
            size.x = Mathf.Min(size.x, maxAllowedSize);
            size.y = Mathf.Min(size.y, maxAllowedSize);
            size.z = Mathf.Min(size.z, maxAllowedSize);

            // Apply minimal padding to prevent edge clipping (2% padding)
            float paddingFactor = 1.02f;
            size *= paddingFactor;

            // Ensure minimum size
            float minSize = 0.1f;
            size.x = Mathf.Max(size.x, minSize);
            size.y = Mathf.Max(size.y, minSize);
            size.z = Mathf.Max(size.z, minSize);

            return new Bounds(center, size);
        }

        /// <summary>
        public static Bounds GetVFXBoundsOverTime(GameObject go, float duration)
        {
            // For VFX, get bounds from current state without resetting
            // VFX should already be initialized by HandleVFXSystems before this is called
            // Calling Reinit() here would reset particles that were already spawned
            VisualEffect[] vfxSystems = go.GetComponentsInChildren<VisualEffect>();

            if (vfxSystems.Length == 0)
            {
                Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
                return GetGlobalBounds(renderers);
            }

            // Get renderer bounds - VFXRenderer bounds update as particles spawn
            // Don't reinit - VFX should already be playing from HandleVFXSystems
            Renderer[] vfxRenderers = go.GetComponentsInChildren<Renderer>();

            Bounds bounds = GetGlobalBounds(vfxRenderers, CustomPrefabPreviewGenerator.PrefabType.VFX, go);

            // Ensure minimum bounds in case VFX hasn't fully spawned yet
            Vector3 size = bounds.size;
            float minSize = 1f; // Minimum 1 unit
            size.x = Mathf.Max(size.x, minSize);
            size.y = Mathf.Max(size.y, minSize);
            size.z = Mathf.Max(size.z, minSize);
            bounds.size = size;

            return bounds;
        }

        /// <summary>
        /// Get FBX animation bounds by sampling across the animation timeline
        /// </summary>
        public static Bounds GetFBXAnimationBoundsOverTime(GameObject go, AnimationClip clip, int sampleCount = 8)
        {
            // Sample bounds across entire animation to frame full motion (e.g., character jumping)
            if (clip == null || clip.length == 0)
            {
                // Fallback to static bounds
                Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
                return GetGlobalBounds(renderers);
            }

            Vector3 minBounds = Vector3.one * float.MaxValue;
            Vector3 maxBounds = Vector3.one * float.MinValue;

            // Sample animation at multiple points in time using direct sampling (parallel-safe)
            for (int sample = 0; sample < sampleCount; sample++)
            {
                float time = clip.length * (sample / (float)sampleCount);

                // Use Mecanim pipeline for humanoid clips (muscle curves), direct sampling for generic
                CustomPrefabPreviewGenerator.SampleAnimationPose(go, clip, time);

                // Force Unity to update transforms after sampling
                Physics.SyncTransforms();

                // Get renderer bounds at this pose
                Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
                foreach (Renderer renderer in renderers)
                {
                    Bounds rendererBounds = renderer.bounds;
                    minBounds = Vector3.Min(minBounds, rendererBounds.min);
                    maxBounds = Vector3.Max(maxBounds, rendererBounds.max);
                }

                // Also sample bone Transform positions to capture skeleton motion
                // This is essential for bone-only FBX files where visualization spheres are static
                // but underlying bone Transforms move with the animation
                Transform[] transforms = go.GetComponentsInChildren<Transform>();
                foreach (Transform bone in transforms)
                {
                    if (bone == go.transform) continue; // Skip root
                    if (Vector3.Distance(bone.position, go.transform.position) < 0.001f) continue;

                    // Use small radius matching bone visualization sphere size (0.03f)
                    float boneRadius = 0.03f;
                    minBounds = Vector3.Min(minBounds, bone.position - Vector3.one * boneRadius);
                    maxBounds = Vector3.Max(maxBounds, bone.position + Vector3.one * boneRadius);
                }
            }

            // Check if we got valid bounds
            if (minBounds.x == float.MaxValue)
            {
                return new Bounds(Vector3.zero, Vector3.one);
            }

            // Create combined bounds from min/max
            Vector3 center = (minBounds + maxBounds) / 2f;
            Vector3 size = maxBounds - minBounds;

            // Add padding
            float paddingFactor = 1.1f; // 10% padding
            size *= paddingFactor;

            // Reset animation to start (frame 0)
            CustomPrefabPreviewGenerator.SampleAnimationPose(go, clip, 0f);
            Physics.SyncTransforms();

            return new Bounds(center, size);
        }

        /// <summary>
        /// Get RectTransform bounds in world space (for UI elements)
        /// </summary>
        public static Bounds GetRectTransformBounds(RectTransform rectTransform)
        {
            Vector3[] worldCorners = new Vector3[4];
            rectTransform.GetWorldCorners(worldCorners);

            // Calculate bounds from world corners
            Vector3 center = (worldCorners[0] + worldCorners[2]) / 2f;
            Vector3 size = new Vector3(
                Vector3.Distance(worldCorners[0], worldCorners[3]), // width
                Vector3.Distance(worldCorners[0], worldCorners[1]), // height
                0f // depth (UI is flat)
            );

            return new Bounds(center, size);
        }

        /// <summary>
        /// Get visible UI bounds, handling ScrollRects and Masks properly.
        /// For ScrollRects, uses the viewport bounds instead of extended content.
        /// For Masks, uses the mask bounds instead of clipped children.
        /// </summary>
        public static Bounds GetVisibleUIBounds(GameObject uiPrefab)
        {
            // Force Canvas layout updates to ensure RectTransforms have correct world positions
            Canvas.ForceUpdateCanvases();

            // Collect all RectTransforms to consider for bounds
            RectTransform[] allRects = uiPrefab.GetComponentsInChildren<RectTransform>(true);

            if (allRects.Length == 0)
            {
                return new Bounds(Vector3.zero, Vector3.one);
            }

            // Build a list of RectTransforms that represent the visible boundaries
            List<RectTransform> visibleRects = new List<RectTransform>();

            // First pass: Identify ScrollRects and Masks that define visible boundaries
            ScrollRect[] scrollRects = uiPrefab.GetComponentsInChildren<ScrollRect>(true);
            Mask[] masks = uiPrefab.GetComponentsInChildren<Mask>(true);
            RectMask2D[] rectMasks = uiPrefab.GetComponentsInChildren<RectMask2D>(true);

            // Create a set of GameObjects that are inside ScrollRect content areas (to exclude them)
            HashSet<GameObject> scrollRectContentObjects = new HashSet<GameObject>();
            HashSet<GameObject> maskedObjects = new HashSet<GameObject>();

            // Handle ScrollRects: use viewport instead of content
            foreach (ScrollRect scrollRect in scrollRects)
            {
                if (scrollRect.viewport != null)
                {
                    // Validate viewport has non-zero size before adding
                    Rect viewportRect = scrollRect.viewport.rect;
                    if (viewportRect.width >= 0.01f && viewportRect.height >= 0.01f)
                    {
                        // Add the viewport as the visible boundary
                        visibleRects.Add(scrollRect.viewport);
                    }

                    // Mark all content AND its descendants as "inside scroll rect" so they're excluded from direct bounds
                    if (scrollRect.content != null)
                    {
                        // Mark the content itself
                        scrollRectContentObjects.Add(scrollRect.content.gameObject);

                        // Mark ALL descendants (children, grandchildren, etc.)
                        RectTransform[] contentDescendants = scrollRect.content.GetComponentsInChildren<RectTransform>(true);
                        foreach (RectTransform descendant in contentDescendants)
                        {
                            scrollRectContentObjects.Add(descendant.gameObject);
                        }
                    }
                }
                else if (scrollRect.content != null)
                {
                    // No viewport specified, use the ScrollRect's own RectTransform as viewport
                    RectTransform scrollRectTransform = scrollRect.GetComponent<RectTransform>();
                    if (scrollRectTransform != null)
                    {
                        // Validate ScrollRect has non-zero size before adding
                        Rect scrollRectRect = scrollRectTransform.rect;
                        if (scrollRectRect.width >= 0.01f && scrollRectRect.height >= 0.01f)
                        {
                            visibleRects.Add(scrollRectTransform);
                        }

                        // Mark content and all descendants as inside scroll rect
                        scrollRectContentObjects.Add(scrollRect.content.gameObject);

                        RectTransform[] contentDescendants = scrollRect.content.GetComponentsInChildren<RectTransform>(true);
                        foreach (RectTransform descendant in contentDescendants)
                        {
                            scrollRectContentObjects.Add(descendant.gameObject);
                        }
                    }
                }
            }

            // Handle Masks: use mask RectTransform as boundary
            // EXCEPTION: If the mask is on a GameObject that also has a ScrollRect,
            // or if the mask is INSIDE a ScrollRect's content, skip adding it
            foreach (Mask mask in masks)
            {
                // Check if this mask's GameObject also has a ScrollRect component
                ScrollRect scrollRectOnMask = mask.GetComponent<ScrollRect>();
                if (scrollRectOnMask != null)
                {
                    // Skip - the ScrollRect's viewport already defines the boundary
                    // But still mark children as masked for proper exclusion
                    Transform[] maskChildren = mask.GetComponentsInChildren<Transform>(true);
                    foreach (Transform child in maskChildren)
                    {
                        if (child != mask.transform)
                        {
                            maskedObjects.Add(child.gameObject);
                        }
                    }
                    continue;
                }

                // Check if this mask is INSIDE a ScrollRect's content area
                if (scrollRectContentObjects.Contains(mask.gameObject))
                {
                    // Skip - this mask is inside scrollable content and shouldn't define the visible boundary
                    // No need to mark children as masked - they're already in scrollRectContentObjects
                    continue;
                }

                RectTransform maskRect = mask.GetComponent<RectTransform>();
                if (maskRect != null)
                {
                    // Validate mask has non-zero size before adding
                    Rect maskRectSize = maskRect.rect;
                    if (maskRectSize.width >= 0.01f && maskRectSize.height >= 0.01f)
                    {
                        visibleRects.Add(maskRect);
                    }

                    // Mark children as masked
                    Transform[] maskChildren = mask.GetComponentsInChildren<Transform>(true);
                    foreach (Transform child in maskChildren)
                    {
                        if (child != mask.transform)
                        {
                            maskedObjects.Add(child.gameObject);
                        }
                    }
                }
            }

            // Handle RectMask2D: use mask RectTransform as boundary
            // EXCEPTION: If the mask is on a GameObject that also has a ScrollRect,
            // or if the mask is INSIDE a ScrollRect's content, skip adding it
            foreach (RectMask2D rectMask in rectMasks)
            {
                // Check if this mask's GameObject also has a ScrollRect component
                ScrollRect scrollRectOnMask = rectMask.GetComponent<ScrollRect>();
                if (scrollRectOnMask != null)
                {
                    // Skip - the ScrollRect's viewport already defines the boundary
                    // But still mark children as masked for proper exclusion
                    Transform[] maskChildren = rectMask.GetComponentsInChildren<Transform>(true);
                    foreach (Transform child in maskChildren)
                    {
                        if (child != rectMask.transform)
                        {
                            maskedObjects.Add(child.gameObject);
                        }
                    }
                    continue;
                }

                // Check if this mask is INSIDE a ScrollRect's content area
                if (scrollRectContentObjects.Contains(rectMask.gameObject))
                {
                    // Skip - this mask is inside scrollable content and shouldn't define the visible boundary
                    // No need to mark children as masked - they're already in scrollRectContentObjects
                    continue;
                }

                RectTransform maskRect = rectMask.GetComponent<RectTransform>();
                if (maskRect != null)
                {
                    // Validate mask has non-zero size before adding
                    Rect maskRectSize = maskRect.rect;
                    if (maskRectSize.width >= 0.01f && maskRectSize.height >= 0.01f)
                    {
                        visibleRects.Add(maskRect);
                    }

                    // Mark children as masked
                    Transform[] maskChildren = rectMask.GetComponentsInChildren<Transform>(true);
                    foreach (Transform child in maskChildren)
                    {
                        if (child != rectMask.transform)
                        {
                            maskedObjects.Add(child.gameObject);
                        }
                    }
                }
            }

            // Second pass: Add RectTransforms that are not inside ScrollRect content or masked areas
            foreach (RectTransform rect in allRects)
            {
                // Skip if this is inside a ScrollRect's content area
                if (scrollRectContentObjects.Contains(rect.gameObject))
                {
                    continue;
                }

                // Skip if this is a masked child (unless it's a viewport/mask itself)
                if (maskedObjects.Contains(rect.gameObject) && !visibleRects.Contains(rect))
                {
                    continue;
                }

                // Skip ScrollRect GameObjects that have a viewport defined
                // (the viewport already represents the visible boundary)
                ScrollRect scrollRect = rect.GetComponent<ScrollRect>();
                if (scrollRect != null && scrollRect.viewport != null)
                {
                    continue;
                }

                // Skip Canvas components themselves
                if (rect.GetComponent<Canvas>() != null)
                {
                    continue;
                }

                // Skip inactive GameObjects
                if (!rect.gameObject.activeInHierarchy)
                {
                    continue;
                }

                // Skip zero or near-zero sized RectTransforms
                Rect rectSize = rect.rect;
                if (rectSize.width < 0.01f || rectSize.height < 0.01f)
                {
                    continue;
                }

                // Skip Image components that are disabled or have near-zero alpha
                // Disabled images won't render, so they shouldn't contribute to visible bounds
                Image image = rect.GetComponent<Image>();
                if (image != null && (!image.enabled || image.color.a < 0.01f))
                {
                    continue;
                }

                // Add to visible rects if not already added
                if (!visibleRects.Contains(rect))
                {
                    visibleRects.Add(rect);
                }
            }

            // If we have no visible rects, fall back to all RectTransforms
            if (visibleRects.Count == 0)
            {
                foreach (RectTransform rect in allRects)
                {
                    if (rect.gameObject.activeInHierarchy)
                    {
                        visibleRects.Add(rect);
                    }
                }
            }

            // Calculate combined bounds from visible RectTransforms
            if (visibleRects.Count == 0)
            {
                return new Bounds(Vector3.zero, Vector3.one);
            }

            Bounds combinedBounds = GetRectTransformBounds(visibleRects[0]);
            for (int i = 1; i < visibleRects.Count; i++)
            {
                combinedBounds.Encapsulate(GetRectTransformBounds(visibleRects[i]));
            }

            return combinedBounds;
        }

        /// <summary>
        /// Get optimized bounds for FBX models by comparing mesh geometry vs renderer bounds
        /// Uses mesh bounds when they provide better framing, with fallback to renderer bounds
        /// </summary>
        public static Bounds GetFBXMeshBounds(Renderer[] renderers)
        {
            if (renderers.Length == 0) return new Bounds(Vector3.zero, Vector3.one);

            // First get standard renderer bounds as fallback
            Bounds rendererBounds = GetGlobalBounds(renderers, CustomPrefabPreviewGenerator.PrefabType.Model);

            Vector3 minBounds = Vector3.one * float.MaxValue;
            Vector3 maxBounds = Vector3.one * float.MinValue;
            bool hasValidMeshBounds = false;

            foreach (Renderer renderer in renderers)
            {
                // Skip non-mesh renderers
                if (renderer is not MeshRenderer && renderer is not SkinnedMeshRenderer) continue;

                Mesh mesh = null;
                Matrix4x4 transformMatrix = renderer.transform.localToWorldMatrix;

                if (renderer is MeshRenderer meshRenderer)
                {
                    MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
                    if (meshFilter != null)
                    {
                        mesh = meshFilter.sharedMesh;
                    }
                }
                else if (renderer is SkinnedMeshRenderer skinnedRenderer)
                {
                    // For static FBX models, use renderer bounds which are usually appropriate
                    // Only use mesh bounds for animated models where renderer bounds might be inflated
                    continue; // Skip SkinnedMeshRenderer for now to avoid over-tight bounds
                }

                if (mesh != null && mesh.vertexCount > 0)
                {
                    Vector3[] vertices = mesh.vertices;

                    foreach (Vector3 vertex in vertices)
                    {
                        Vector3 worldVertex = transformMatrix.MultiplyPoint3x4(vertex);
                        minBounds = Vector3.Min(minBounds, worldVertex);
                        maxBounds = Vector3.Max(maxBounds, worldVertex);
                    }

                    hasValidMeshBounds = true;
                }
            }

            if (!hasValidMeshBounds)
            {
                // Use renderer bounds if mesh-based bounds failed
                return rendererBounds;
            }

            Vector3 center = (minBounds + maxBounds) / 2f;
            Vector3 meshSize = maxBounds - minBounds;

            // Ensure minimum size
            Vector3 minSize = Vector3.one * 0.01f;
            meshSize = Vector3.Max(meshSize, minSize);

            Bounds meshBounds = new Bounds(center, meshSize);

            // Use mesh bounds if they're larger than renderer bounds (renderer bounds might be too tight)
            // or if they're reasonably close. Never use mesh bounds smaller than renderer bounds.
            float rendererVolume = rendererBounds.size.x * rendererBounds.size.y * rendererBounds.size.z;
            float meshVolume = meshBounds.size.x * meshBounds.size.y * meshBounds.size.z;

            if (meshVolume >= rendererVolume * 0.8f)
            {
                // Mesh bounds are at least 80% the size of renderer bounds, use mesh bounds
                meshBounds.size *= 1.0f; // No additional padding needed
                return meshBounds;
            }
            else
            {
                // Mesh bounds are significantly smaller, use renderer bounds which are more conservative
                return rendererBounds;
            }
        }

        /// <summary>
        /// Finds the hierarchy-root particle systems in a prefab. A root PS is one whose
        /// parent chain (up to the prefab root) contains no other ParticleSystem component.
        /// When the first PS in GetComponentsInChildren order IS the common ancestor of all
        /// others (the typical case), this returns just that one system — identical to the
        /// previous particleSystems[0] behavior. For branched hierarchies where no single PS
        /// is the ancestor of all siblings, this returns each independent sub-tree root so
        /// that Simulate(withChildren:true) on each one covers the entire hierarchy.
        /// </summary>
        private static List<ParticleSystem> FindRootParticleSystems(ParticleSystem[] allSystems, GameObject prefabRoot)
        {
            if (allSystems.Length == 0) return new List<ParticleSystem>();

            // Fast path: if the first system's GameObject is the prefab root or an ancestor
            // of all others, it already covers everything via withChildren=true.
            ParticleSystem first = allSystems[0];
            bool firstIsAncestorOfAll = true;
            Transform firstTransform = first.transform;
            for (int i = 1; i < allSystems.Length; i++)
            {
                if (!allSystems[i].transform.IsChildOf(firstTransform))
                {
                    firstIsAncestorOfAll = false;
                    break;
                }
            }
            if (firstIsAncestorOfAll)
            {
                return new List<ParticleSystem> {first};
            }

            // Slow path: collect every PS that has no PS-ancestor between itself and prefabRoot.
            HashSet<ParticleSystem> psSet = new HashSet<ParticleSystem>(allSystems);
            List<ParticleSystem> roots = new List<ParticleSystem>();
            foreach (ParticleSystem ps in allSystems)
            {
                bool hasAncestorPS = false;
                Transform t = ps.transform.parent;
                Transform rootTransform = prefabRoot.transform;
                while (t != null && t != rootTransform)
                {
                    ParticleSystem parentPS = t.GetComponent<ParticleSystem>();
                    if (parentPS != null && psSet.Contains(parentPS))
                    {
                        hasAncestorPS = true;
                        break;
                    }
                    t = t.parent;
                }
                if (!hasAncestorPS)
                {
                    roots.Add(ps);
                }
            }
            return roots;
        }
    }
}