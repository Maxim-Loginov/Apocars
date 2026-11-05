using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Apocars
{
    /// Unity's own components keep their settings in native code, not in C# fields: they are read and written
    /// through these properties (the ones that matter for a vehicle and its parts; anything else through the
    /// type's public read/write properties, minus the ones that would create copies or lie).
    internal static class BuiltIn
    {
        private static readonly Dictionary<Type, string[]> Known = new Dictionary<Type, string[]>
        {
            [typeof(Rigidbody)] = new[] { "mass", "drag", "angularDrag", "useGravity", "isKinematic", "interpolation", "collisionDetectionMode",
                "constraints", "maxAngularVelocity", "maxDepenetrationVelocity", "solverIterations", "solverVelocityIterations", "sleepThreshold",
                "detectCollisions" },
            [typeof(BoxCollider)] = new[] { "enabled", "isTrigger", "sharedMaterial", "contactOffset", "center", "size" },
            [typeof(SphereCollider)] = new[] { "enabled", "isTrigger", "sharedMaterial", "contactOffset", "center", "radius" },
            [typeof(CapsuleCollider)] = new[] { "enabled", "isTrigger", "sharedMaterial", "contactOffset", "center", "radius", "height", "direction" },
            [typeof(MeshCollider)] = new[] { "enabled", "isTrigger", "sharedMaterial", "contactOffset", "sharedMesh", "convex", "cookingOptions" },
            [typeof(MeshFilter)] = new[] { "sharedMesh" },
            [typeof(MeshRenderer)] = new[] { "enabled", "sharedMaterials", "shadowCastingMode", "receiveShadows", "lightProbeUsage", "reflectionProbeUsage",
                "motionVectorGenerationMode", "allowOcclusionWhenDynamic", "renderingLayerMask", "sortingOrder", "sortingLayerID" },
            [typeof(SkinnedMeshRenderer)] = new[] { "enabled", "sharedMesh", "sharedMaterials", "bones", "rootBone", "quality", "updateWhenOffscreen",
                "localBounds", "shadowCastingMode", "receiveShadows" },
            [typeof(Light)] = new[] { "enabled", "type", "color", "intensity", "range", "spotAngle", "innerSpotAngle", "shadows", "shadowStrength", "shadowBias",
                "shadowNormalBias", "shadowNearPlane", "cookie", "cookieSize", "flare", "renderMode", "cullingMask", "bounceIntensity",
                "colorTemperature", "useColorTemperature" },
            [typeof(AudioSource)] = new[] { "enabled", "clip", "outputAudioMixerGroup", "mute", "bypassEffects", "bypassListenerEffects", "bypassReverbZones",
                "playOnAwake", "loop", "priority", "volume", "pitch", "panStereo", "spatialBlend", "reverbZoneMix", "dopplerLevel", "spread",
                "rolloffMode", "minDistance", "maxDistance", "spatialize", "spatializePostEffects", "ignoreListenerVolume", "ignoreListenerPause" },
            [typeof(AudioListener)] = new[] { "enabled" },
            [typeof(Camera)] = new[] { "enabled", "fieldOfView", "nearClipPlane", "farClipPlane", "cullingMask", "depth", "clearFlags", "backgroundColor",
                "orthographic", "orthographicSize", "rect", "allowHDR", "allowMSAA", "useOcclusionCulling", "targetDisplay" },
            [typeof(FixedJoint)] = Joint(),
            [typeof(HingeJoint)] = Joint("useMotor", "motor", "useLimits", "limits", "useSpring", "spring"),
            [typeof(SpringJoint)] = Joint("spring", "damper", "minDistance", "maxDistance", "tolerance"),
            [typeof(ConfigurableJoint)] = Joint("secondaryAxis", "xMotion", "yMotion", "zMotion", "angularXMotion", "angularYMotion", "angularZMotion",
                "linearLimit", "lowAngularXLimit", "highAngularXLimit", "angularYLimit", "angularZLimit", "linearLimitSpring", "angularXLimitSpring",
                "angularYZLimitSpring", "targetPosition", "targetVelocity", "xDrive", "yDrive", "zDrive", "targetRotation", "targetAngularVelocity",
                "rotationDriveMode", "angularXDrive", "angularYZDrive", "slerpDrive", "projectionMode", "projectionDistance", "projectionAngle",
                "configuredInWorldSpace", "swapBodies"),
            [typeof(CharacterJoint)] = Joint("swingAxis", "twistLimitSpring", "swingLimitSpring", "lowTwistLimit", "highTwistLimit", "swing1Limit",
                "swing2Limit", "enableProjection", "projectionDistance", "projectionAngle"),
            [typeof(Animator)] = new[] { "enabled", "runtimeAnimatorController", "avatar", "applyRootMotion", "updateMode", "cullingMode" },
            [typeof(LODGroup)] = new[] { "enabled", "fadeMode", "animateCrossFading", "localReferencePoint", "size" },
            [typeof(TrailRenderer)] = new[] { "enabled", "sharedMaterials", "time", "startWidth", "endWidth", "widthCurve", "colorGradient", "minVertexDistance",
                "autodestruct", "emitting", "shadowCastingMode", "receiveShadows" },
            [typeof(LineRenderer)] = new[] { "enabled", "sharedMaterials", "startWidth", "endWidth", "widthCurve", "colorGradient", "useWorldSpace", "loop",
                "shadowCastingMode", "receiveShadows" },
        };

        private static string[] Joint(params string[] more) =>
            new[] { "connectedBody", "anchor", "axis", "autoConfigureConnectedAnchor", "connectedAnchor", "breakForce", "breakTorque",
                    "enableCollision", "enablePreprocessing", "massScale", "connectedMassScale" }.Concat(more).ToArray();

        /// Generic fallback: names that create copies, are derived, or are the GameObject's own business.
        private static readonly HashSet<string> Never = new HashSet<string>
        {
            "material", "materials", "mesh", "name", "tag", "hideFlags", "transform", "gameObject", "isActiveAndEnabled",
            "runInEditMode", "useGUILayout", "velocity", "angularVelocity", "position", "rotation", "worldCenterOfMass",
            "isPlaying", "time", "timeSamples", "bounds", "localToWorldMatrix", "worldToLocalMatrix",
        };

        public static IEnumerable<string> PropertiesOf(Type t)
        {
            for (var k = t; k != null && k != typeof(Component); k = k.BaseType)
                if (Known.TryGetValue(k, out var names)) return names;
            return t.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && !Never.Contains(p.Name)
                            && !p.IsDefined(typeof(ObsoleteAttribute), true) && Snapshot.Serializable(p.PropertyType)
                            && p.DeclaringType != typeof(Component) && p.DeclaringType != typeof(UnityEngine.Object))
                .Select(p => p.Name);
        }

        public static Dictionary<string, object> Read(Component c, Snapshot snap)
        {
            var t = c.GetType();
            var d = new Dictionary<string, object>();
            foreach (var name in PropertiesOf(t))
            {
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (p == null || !p.CanRead) continue;
                try
                {
                    var v = p.GetValue(c, null);
                    d[name] = v != null && v.GetType().IsValueType && !v.GetType().IsPrimitive && !v.GetType().IsEnum && !Snapshot.UnityStruct(v.GetType())
                        ? Struct(v, snap)   // JointDrive, SoftJointLimit, JointSpring...: their properties
                        : snap.Value(v, p.PropertyType, 0);
                }
                catch (Exception e) { d[name] = new Dictionary<string, object> { ["$error"] = (e.InnerException ?? e).Message }; }
            }
            // custom centre of mass: Unity 2020 has no flag for it, so both are kept; the builder decides
            if (c is Rigidbody rb) d["centerOfMass"] = Snapshot.V(rb.centerOfMass);
            if (c.GetType().Name == "ParticleSystem") d["$note"] = "particle system: rebuilt from the game's prefab by name";
            return d;
        }

        private static Dictionary<string, object> Struct(object v, Snapshot snap)
        {
            var t = v.GetType();
            var d = new Dictionary<string, object>();
            // some keep their settings in public fields (JointSpring: spring, damper, targetPosition), some in properties
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public))
                try { d[f.Name] = snap.Value(f.GetValue(v), f.FieldType, 1); } catch { }
            foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && !p.IsDefined(typeof(ObsoleteAttribute), true))
                    try { d[p.Name] = snap.Value(p.GetValue(v, null), p.PropertyType, 1); } catch { }
            return d;
        }
    }
}
