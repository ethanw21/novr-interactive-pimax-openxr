// Unity rendering, the XR display and the native OpenXR plugin are boundaries.
// The production policy and component run unchanged; nothing is drawn and no
// runtime is contacted. Signatures mirror the Unity members the component uses.
namespace UnityEngine
{
    public enum CameraType
    {
        Game = 1, SceneView = 2
    }
    public enum StereoTargetEyeMask
    {
        None, Left, Right, Both
    }
    public enum CubemapFace
    {
        Unknown = -1, PositiveX
    }
    public enum HideFlags
    {
        None = 0, HideAndDontSave = 61
    }
    public readonly record struct Vector3(float x, float y, float z);
    public readonly record struct Color(float r, float g, float b, float a)
    {
        public static Color white => new(1, 1, 1, 1);
    }
    public readonly record struct Matrix4x4(int Id)
    {
        public static Matrix4x4 identity => new(1);
    }
    public struct RenderTextureDescriptor
    {
        public int width, height, msaaSamples;
        public string graphicsFormat;
    }
    public sealed class RenderTexture;
    public sealed class Camera
    {
        public CameraType cameraType = CameraType.Game;
        public RenderTexture? targetTexture;
        public StereoTargetEyeMask stereoTargetEye = StereoTargetEyeMask.Both;
    }
    public sealed class Shader
    {
        public static bool Available = true;
        public static readonly List<string> Requests = new();
        public static Shader? Find(string name)
        {
            Requests.Add(name);
            return Available ? new Shader() : null;
        }
    }
    public sealed class Material
    {
        public static int Created;
        public readonly Shader Shader;
        public string name = "";
        public HideFlags hideFlags;
        public readonly Dictionary<string, float> Floats = new();
        public readonly Dictionary<string, Color> Colors = new();
        public bool Destroyed;
        public Material(Shader shader)
        {
            Created++;
            Shader = shader;
        }
        public void SetFloat(string property, float value) => Floats[property] = value;
        public void SetColor(string property, Color value) => Colors[property] = value;
    }
    public sealed class Mesh
    {
        public string name = "";
        public HideFlags hideFlags;
        public Vector3[] vertices = Array.Empty<Vector3>();
        public Color[] colors = Array.Empty<Color>();
        public int[] triangles = Array.Empty<int>();
        public bool Destroyed;
    }
    public class MonoBehaviour
    {
        public static void Destroy(object target)
        {
            if (target is Material material)
                material.Destroyed = true;
            if (target is Mesh mesh)
                mesh.Destroyed = true;
        }
    }
    public static class Debug
    {
        public static readonly List<string> Logs = new(), Warnings = new();
        public static void Log(object message) => Logs.Add(message.ToString()!);
        public static void LogWarning(object message) => Warnings.Add(message.ToString()!);
    }
    public static class SubsystemManager
    {
        public static readonly List<object> Subsystems = new();
        public static void GetSubsystems<T>(List<T> subsystems)
        {
            subsystems.Clear();
            subsystems.AddRange(Subsystems.OfType<T>());
        }
    }
}
namespace UnityEngine.Rendering
{
    public enum BlendMode
    {
        Zero = 0, One = 1, SrcAlpha = 5, OneMinusSrcColor = 6, OneMinusSrcAlpha = 10
    }
    public enum CompareFunction
    {
        Disabled = 0, LessEqual = 4, Always = 8
    }
    public enum CullMode
    {
        Off = 0, Front = 1, Back = 2
    }
    public readonly record struct RenderTargetIdentifier(string Name);
    public sealed class CommandBuffer(string name)
    {
        public readonly string Name = name;
        public readonly List<string> Calls = new();
        public Mesh? Mesh;
        public Material? Material;
        public void SetViewProjectionMatrices(Matrix4x4 view, Matrix4x4 projection) =>
            Calls.Add(view == Matrix4x4.identity && projection == Matrix4x4.identity ? "clip-space" : "other-matrices");
        public void SetRenderTarget(RenderTargetIdentifier target, int mipLevel, CubemapFace face, int depthSlice) =>
            Calls.Add($"target {target.Name} mip {mipLevel} face {face} slice {depthSlice}");
        public void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int submeshIndex, int shaderPass)
        {
            Mesh = mesh;
            Material = material;
            Calls.Add(matrix == Matrix4x4.identity && submeshIndex == 0 && shaderPass == 0 ? "draw" : "other-draw");
        }
    }
    public static class CommandBufferPool
    {
        public static int Outstanding;
        public static CommandBuffer Get(string name)
        {
            Outstanding++;
            return new CommandBuffer(name);
        }
        public static void Release(CommandBuffer command) => Outstanding--;
    }
    public sealed class ScriptableRenderContext
    {
        public readonly List<CommandBuffer> Executed = new();
        public int Submits;
        public void ExecuteCommandBuffer(CommandBuffer command) => Executed.Add(command);
        public void Submit() => Submits++;
    }
    public static class RenderPipelineManager
    {
        public static event Action<ScriptableRenderContext, Camera[]>? endFrameRendering;
        public static int Subscribers => endFrameRendering?.GetInvocationList().Length ?? 0;
        public static void EndFrame(ScriptableRenderContext context, params Camera[] cameras) =>
            endFrameRendering?.Invoke(context, cameras);
    }
}
namespace UnityEngine.XR
{
    using UnityEngine.Rendering;
    public static class XRSettings
    {
        public static bool enabled;
    }
    public sealed class XRDisplaySubsystem
    {
        public bool running = true;
        public bool ThrowOnPass;
        public readonly List<XRRenderPass> Passes = new();
        public int GetRenderPassCount() => Passes.Count;
        public void GetRenderPass(int renderPassIndex, out XRRenderPass renderPass)
        {
            if (ThrowOnPass)
                throw new InvalidOperationException("render pass unavailable");
            renderPass = Passes[renderPassIndex];
        }
        public struct XRRenderParameter
        {
            public int textureArraySlice;
        }
        public struct XRRenderPass
        {
            public RenderTargetIdentifier renderTarget;
            public RenderTextureDescriptor renderTargetDesc;
            public int[] Slices;
            public readonly int GetRenderParameterCount() => Slices.Length;
            public readonly void GetRenderParameter(Camera camera, int renderParameterIndex, out XRRenderParameter renderParameter) =>
                renderParameter = new XRRenderParameter { textureArraySlice = Slices[renderParameterIndex] };
        }
    }
}
namespace UnityEngine.XR.OpenXR
{
    public static class OpenXRRuntime
    {
        public static string Name = "";
        public static bool Throw;
        public static string name => Throw ? throw new DllNotFoundException("UnityOpenXR") : Name;
    }
}
namespace NOVR
{
    internal sealed class ConfigEntry<T>
    {
        internal T Value = default!;
    }
    internal sealed class ModConfiguration
    {
        internal static readonly ModConfiguration Instance = new();
        internal readonly ConfigEntry<Compatibility.HeadsetImageOpacity> HeadsetImageOpacity = new();
    }
}
