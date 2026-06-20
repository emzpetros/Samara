using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AssetInventory
{
    /// <summary>
    /// Centralized camera creation and positioning for all preview types.
    /// Consolidates camera logic from PreviewManager, CustomPreviewStage, and PreviewSceneSetup.
    /// </summary>
    public static class PreviewCameraSetup
    {
        public const float DefaultPreviewFOV = 30f;
        public const float DefaultNearClip = 0.01f;

        /// <summary>
        /// Create and configure a preview camera in the specified scene
        /// </summary>
        public static Camera CreatePreviewCamera(Scene scene, bool addToScene = true)
        {
            GameObject camGO = new GameObject("PreviewCamera");
            if (addToScene && scene.IsValid())
            {
                SceneManager.MoveGameObjectToScene(camGO, scene);
            }

            Camera camera = camGO.AddComponent<Camera>();
            camera.enabled = false;
            camera.tag = "MainCamera";
            camera.scene = scene;
            camera.nearClipPlane = DefaultNearClip;
            camera.farClipPlane = 100000;
            camera.fieldOfView = DefaultPreviewFOV;
            camera.depthTextureMode = DepthTextureMode.Depth;
            camera.clearFlags = CameraClearFlags.Color;
            camera.cullingMask = -1; // Render all layers explicitly

            // CRITICAL: Set aspect explicitly to prevent Unity from using screen aspect ratio.
            // Without this, camera.aspect defaults to the display's aspect (~1.78 for 16:9),
            // causing WorldToViewportPoint() to return incorrect coordinates during camera
            // positioning, which shifts non-square objects off-center in previews.
            camera.aspect = 1.0f;

            SetupCameraBackground(camera);
            SetupRenderPipelineComponents(camera);

            return camera;
        }

        /// <summary>
        /// Setup camera background based on configuration
        /// </summary>
        public static void SetupCameraBackground(Camera camera)
        {
            switch (AI.Config.cpBackgroundType)
            {
                case CustomPreviewBackgroundType.Transparent:
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = Color.clear;
                    break;

                case CustomPreviewBackgroundType.TwoColorGradient:
                case CustomPreviewBackgroundType.FourColorGradient:
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = Color.black; // Will be covered by gradient quad
                    break;

                case CustomPreviewBackgroundType.SolidColor:
                default:
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    Color bgColor = GetBackgroundColor();
                    camera.backgroundColor = bgColor;
                    break;
            }
        }

        /// <summary>
        /// Get background color based on render pipeline
        /// </summary>
        public static Color GetBackgroundColor()
        {
            if (AssetUtils.IsOnHDRP())
            {
                Color bgColorHDRP = new Color(34f / 255, 34f / 255, 34f / 255);
                if (!string.IsNullOrEmpty(AI.Config.cpBackgroundColorHDRP) &&
                    ColorUtility.TryParseHtmlString("#" + AI.Config.cpBackgroundColorHDRP, out Color bch))
                {
                    bgColorHDRP = bch;
                }
                return bgColorHDRP;
            }
            else
            {
                Color bgColor = new Color(82f / 255, 82f / 255, 82f / 255);
                if (!string.IsNullOrEmpty(AI.Config.cpBackgroundColor) &&
                    ColorUtility.TryParseHtmlString("#" + AI.Config.cpBackgroundColor, out Color bc))
                {
                    bgColor = bc;
                }
                return bgColor;
            }
        }

        /// <summary>
        /// Setup render pipeline specific components (HDRP, URP)
        /// </summary>
        private static void SetupRenderPipelineComponents(Camera camera)
        {
            if (AssetUtils.IsOnHDRP())
            {
#if USE_HDRP
                UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData cameraData = camera.gameObject.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
                cameraData.clearColorMode = UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData.ClearColorMode.Color;
                cameraData.backgroundColorHDR = camera.backgroundColor;
#endif
            }
#if USE_URP
            else if (AssetUtils.IsOnURP())
            {
                camera.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            }
#endif
        }

        /// <summary>
        /// Position camera for scene rendering based on scene bounds
        /// </summary>
        public static void PositionCameraForScene(Camera camera, Bounds bounds)
        {
            // Use projected bounds calculation for proper framing
            float distance = CalculateCameraDistance(bounds, camera);

            // Position camera
            camera.transform.position = new Vector3(bounds.center.x, bounds.center.y + distance, bounds.center.z);
            camera.transform.LookAt(bounds.center);

            // Apply custom angles
            camera.transform.RotateAround(bounds.center, Vector3.left, AI.Config.cpCameraAngleX);
            camera.transform.RotateAround(bounds.center, Vector3.up, AI.Config.cpCameraAngleY);

            // Compensate for perspective shift when viewing at an angle
            // When camera looks down at the object, parts above center appear larger (closer)
            // and parts below appear smaller (farther), shifting the visual center downward.
            // Looking at a point above geometric center compensates for this effect.
            float verticalAngleRad = AI.Config.cpCameraAngleX * Mathf.Deg2Rad;
            float perspectiveCompensation = bounds.extents.y * Mathf.Sin(verticalAngleRad) * 0.15f;
            Vector3 lookTarget = bounds.center + Vector3.up * perspectiveCompensation;
            camera.transform.LookAt(lookTarget);
        }

        /// <summary>
        /// Calculate the optimal camera distance for framing an object with configurable padding.
        /// Uses a hybrid approach: projected bounds calculation verified by corner projection
        /// to ensure all 8 bounding box corners fit within the view frustum.
        /// </summary>
        /// <param name="bounds">The bounding box of the object to frame</param>
        /// <param name="camera">The camera (for FOV)</param>
        /// <param name="paddingPercent">Padding as a percentage (e.g., 2 for 2%)</param>
        /// <param name="angleX">Vertical camera angle in degrees (0 = eye level, 90 = top-down)</param>
        /// <param name="angleY">Horizontal camera angle in degrees (rotation around Y axis)</param>
        /// <param name="fillFraction">Target viewport fill (1 = fit exactly). Lower values bring the camera closer.</param>
        /// <returns>The optimal distance from the camera to the bounds center</returns>
        public static float CalculateCameraDistance(Bounds bounds, Camera camera, float paddingPercent, float angleX, float angleY, float fillFraction = 1f)
        {
            // Get the bounds extents (half-sizes)
            float extentX = bounds.extents.x;
            float extentY = bounds.extents.y;
            float extentZ = bounds.extents.z;

            // Calculate vertical FOV in radians
            float vertFOV = camera.fieldOfView * Mathf.Deg2Rad;

            // Calculate horizontal FOV from vertical FOV and aspect ratio
            // For square previews, aspect ratio is 1:1, so horizontal FOV equals vertical FOV
            float aspectRatio = 1.0f;
            float horizFOV = 2f * Mathf.Atan(Mathf.Tan(vertFOV / 2f) * aspectRatio);

            // Convert angles to radians
            float angleXRad = angleX * Mathf.Deg2Rad;
            float angleYRad = angleY * Mathf.Deg2Rad;

            // Calculate apparent width and height from the view direction
            // When viewing at angles, we see a rotated bounding box projection

            // Apparent width: combination of X and Z extents based on horizontal rotation
            float apparentWidth = extentX * Mathf.Abs(Mathf.Cos(angleYRad)) + extentZ * Mathf.Abs(Mathf.Sin(angleYRad));

            // Apparent height: Y extent projected through vertical angle, plus contribution from depth
            // The depth contribution depends on how much the horizontal plane is visible due to the downward angle
            float horizontalPlaneExtent = extentX * Mathf.Abs(Mathf.Sin(angleYRad)) + extentZ * Mathf.Abs(Mathf.Cos(angleYRad));
            float apparentHeight = extentY * Mathf.Abs(Mathf.Cos(angleXRad)) + horizontalPlaneExtent * Mathf.Abs(Mathf.Sin(angleXRad));

            // Projected aspect ratio (height vs. width) used to detect very tall/slender objects.
            // Extreme tallness needs more conservative distance so top/bottom corners do not clip.
            float heightOverWidth = apparentHeight / Mathf.Max(apparentWidth, 0.0001f);

            // Map projected aspect ratio into [0, 1] "tallness":
            //   height/width <= 2 → tallness = 0       (normal objects)
            //   height/width  ~ 4 → tallness ~ 0.5
            //   height/width  >= 6 → tallness ≈ 1     (very tall, slender objects)
            float tallness = Mathf.Clamp01((heightOverWidth - 2f) / 4f);

            // Calculate depth extent - how far the bounding box extends toward/away from camera
            // This is critical for wide/deep objects viewed at steep angles (like rooms from above)
            // The far corners are further from camera than the center, appearing smaller due to perspective
            float depthExtent = horizontalPlaneExtent * Mathf.Abs(Mathf.Cos(angleXRad)) + extentY * Mathf.Abs(Mathf.Sin(angleXRad));

            // Calculate required distance for each axis to fit the object in the view frustum
            // Using: distance = extent / tan(FOV/2)
            float distanceForWidth = apparentWidth / Mathf.Tan(horizFOV / 2f);
            float distanceForHeight = apparentHeight / Mathf.Tan(vertFOV / 2f);

            // Use the larger distance to ensure object fits both dimensions
            float projectedBoundsDistance = Mathf.Max(distanceForWidth, distanceForHeight);

            // Calculate corner-based distance to ensure all 8 bounding box corners fit
            // This is more accurate for objects with extreme aspect ratios (like tall letters)
            // when viewed at steep angles where perspective distortion is significant
            float cornerBasedDistance = CalculateDistanceForCorners(bounds, vertFOV, horizFOV, angleXRad, angleYRad);

            // Calculate bounding sphere distance as a fallback (most conservative approach)
            // This is the same method used by Unity's Asset Store Tools - guarantees all points fit
            // regardless of viewing angle by using the encapsulating sphere
            float boundingSphereDiameter = (bounds.max - bounds.min).magnitude;
            float boundingSphereRadius = boundingSphereDiameter / 2f;

            // Account for perspective compensation: when viewing at an angle, the look target shifts up.
            // Use a reduced factor so this safety margin does not overshoot Unity's native framing.
            float perspectiveCompensation = extentY * Mathf.Sin(angleXRad) * 0.08f;
            float effectiveRadius = boundingSphereRadius + perspectiveCompensation;

            float halfFOV = Mathf.Min(vertFOV, horizFOV) / 2f; // Use smaller FOV for safety
            float boundingSphereDistance = effectiveRadius / Mathf.Sin(halfFOV);

            // Start from the strict distance that fits projected size and all 8 corners.
            float distance = Mathf.Max(projectedBoundsDistance, cornerBasedDistance);

            if (paddingPercent > 0.0001f)
            {
                // For non-zero padding, add small conservative margins so content
                // remains comfortably inside the frame even for deep objects.

                // Add quarter of the depth extent to compensate for perspective on deep objects
                distance += depthExtent * 0.25f;

                // Only let the conservative sphere fallback add a small headroom (up to 2%).
                if (boundingSphereDistance > distance)
                {
                    float maxAllowed = distance * 1.02f;
                    distance = Mathf.Min(boundingSphereDistance, maxAllowed);
                }
            }
            else
            {
                // For zero padding we want the object to be very close to the frame, but still
                // safely inside. The corner-based distance is conservative because it fully
                // accounts for depth; we can blend it back toward the 2D projected distance
                // to tighten framing while keeping all corners visible.
                float baseProjected = Mathf.Max(distanceForWidth, distanceForHeight);
                float delta = cornerBasedDistance - baseProjected;

                if (delta > 0f)
                {
                    // For normal objects, keep a tighter fit by blending toward the 2D projection.
                    // For very tall, slender objects, move toward the full corner-based distance
                    // to avoid top/bottom clipping.
                    float tightenFactor = Mathf.Lerp(0.5f, 1.0f, tallness);

                    distance = baseProjected + delta * tightenFactor;
                }
                else
                {
                    distance = cornerBasedDistance;
                }
            }

            // Apply a small tall-object safety adjustment to the fill fraction when there is
            // effectively no user padding. This slightly reduces the effective fill for very
            // tall, slender objects so their top and bottom stay within the frame, while
            // leaving normal objects unchanged.
            if (paddingPercent <= 0.0001f)
            {
                float tallSafety = Mathf.Lerp(1.0f, 0.97f, tallness); // up to -3% for very tall objects
                fillFraction *= tallSafety;
            }

            // Apply target fill to intentionally move closer for padded bounds (e.g., VFX)
            distance *= Mathf.Max(0.01f, fillFraction);

            // Apply padding (e.g., 2% padding = 1.02 multiplier)
            distance *= (1f + paddingPercent / 100f);

            // Ensure a minimum distance to prevent camera being inside the object
            float minDistance = bounds.extents.magnitude * 0.5f;
            distance = Mathf.Max(distance, minDistance);

            return distance;
        }

        /// <summary>
        /// Get all 8 corners of a bounding box
        /// </summary>
        private static Vector3[] GetBoundsCorners(Bounds bounds)
        {
            Vector3 center = bounds.center;
            Vector3 extents = bounds.extents;

            return new Vector3[]
            {
                center + new Vector3(-extents.x, -extents.y, -extents.z),
                center + new Vector3(-extents.x, -extents.y, +extents.z),
                center + new Vector3(-extents.x, +extents.y, -extents.z),
                center + new Vector3(-extents.x, +extents.y, +extents.z),
                center + new Vector3(+extents.x, -extents.y, -extents.z),
                center + new Vector3(+extents.x, -extents.y, +extents.z),
                center + new Vector3(+extents.x, +extents.y, -extents.z),
                center + new Vector3(+extents.x, +extents.y, +extents.z),
            };
        }

        /// <summary>
        /// Calculate the minimum camera distance required to fit all 8 bounding box corners
        /// within the view frustum. This accounts for perspective distortion where corners
        /// closer to the camera appear larger than expected from simple projection.
        /// </summary>
        private static float CalculateDistanceForCorners(Bounds bounds, float vertFOV, float horizFOV, float angleXRad, float angleYRad)
        {
            Vector3[] corners = GetBoundsCorners(bounds);
            Vector3 center = bounds.center;

            // Calculate the camera's view direction based on angles
            // Start facing -Z (forward), rotate around X (pitch down), then around Y (yaw)
            // The camera will be positioned along the opposite of this direction

            // Calculate camera direction vectors
            // After angleX rotation (pitch): camera looks down from above
            // After angleY rotation (yaw): camera rotates horizontally around the object
            float cosX = Mathf.Cos(angleXRad);
            float sinX = Mathf.Sin(angleXRad);
            float cosY = Mathf.Cos(angleYRad);
            float sinY = Mathf.Sin(angleYRad);

            // Camera forward direction (what the camera is looking at - towards center)
            // After rotating, the camera looks from a position where:
            // - angleX rotates around the left axis (pitches down)
            // - angleY rotates around the up axis (yaws around)
            Vector3 cameraForward = new Vector3(
                -sinY * cosX,
                -sinX,
                -cosY * cosX
            ).normalized;

            // Camera up direction (perpendicular to forward, in the vertical plane)
            Vector3 cameraUp = new Vector3(
                -sinY * sinX,
                cosX,
                -cosY * sinX
            ).normalized;

            // Camera right direction (perpendicular to both forward and up)
            Vector3 cameraRight = Vector3.Cross(cameraUp, cameraForward).normalized;

            // For each corner, calculate the required distance to fit it in the frustum
            float maxRequiredDistance = 0f;
            float halfVertFOV = vertFOV / 2f;
            float halfHorizFOV = horizFOV / 2f;
            float tanHalfVertFOV = Mathf.Tan(halfVertFOV);
            float tanHalfHorizFOV = Mathf.Tan(halfHorizFOV);

            foreach (Vector3 corner in corners)
            {
                // Vector from bounds center to this corner
                Vector3 toCorner = corner - center;

                // Project corner offset onto camera coordinate system
                // depth: how far along the camera's view direction (positive = closer to camera than center)
                // vertical: how far up/down from the view axis
                // horizontal: how far left/right from the view axis
                float depth = -Vector3.Dot(toCorner, cameraForward);
                float vertical = Vector3.Dot(toCorner, cameraUp);
                float horizontal = Vector3.Dot(toCorner, cameraRight);

                // Calculate required distance for this corner
                // If camera is at distance d from center, corner is at distance (d - depth) from camera
                // (depth > 0 means corner is closer to camera, so distance is smaller)
                // The frustum half-height at distance z from camera is: z * tan(halfFOV)
                // For corner to fit: |vertical| <= (d - depth) * tan(halfVertFOV)
                // Solving for d: d >= |vertical| / tan(halfVertFOV) + depth

                float requiredForVertical = Mathf.Abs(vertical) / tanHalfVertFOV + depth;
                float requiredForHorizontal = Mathf.Abs(horizontal) / tanHalfHorizFOV + depth;

                float requiredForCorner = Mathf.Max(requiredForVertical, requiredForHorizontal);
                maxRequiredDistance = Mathf.Max(maxRequiredDistance, requiredForCorner);
            }

            return maxRequiredDistance;
        }

        /// <summary>
        /// Simplified overload using current config settings for angles and padding
        /// </summary>
        public static float CalculateCameraDistance(Bounds bounds, Camera camera)
        {
            return CalculateCameraDistance(bounds, camera, AI.Config.cpFramingPadding, AI.Config.cpCameraAngleX, AI.Config.cpCameraAngleY);
        }

        /// <summary>
        /// Position perspective camera for 3D object rendering
        /// </summary>
        public static void PositionCameraFor3D(Camera camera, Bounds bounds)
        {
            // Use projected bounds calculation for proper framing
            float distance = CalculateCameraDistance(bounds, camera);

            // Position camera
            camera.transform.position = new Vector3(bounds.center.x, bounds.center.y + distance, bounds.center.z);
            camera.transform.LookAt(bounds.center);

            // Apply custom angles
            camera.transform.RotateAround(bounds.center, Vector3.left, AI.Config.cpCameraAngleX);
            camera.transform.RotateAround(bounds.center, Vector3.up, AI.Config.cpCameraAngleY);

            // Compensate for perspective shift when viewing at an angle
            // When camera looks down at the object, parts above center appear larger (closer)
            // and parts below appear smaller (farther), shifting the visual center downward.
            // Looking at a point above geometric center compensates for this effect.
            float verticalAngleRad = AI.Config.cpCameraAngleX * Mathf.Deg2Rad;
            float perspectiveCompensation = bounds.extents.y * Mathf.Sin(verticalAngleRad) * 0.15f;
            Vector3 lookTarget = bounds.center + Vector3.up * perspectiveCompensation;
            camera.transform.LookAt(lookTarget);
        }
    }
}