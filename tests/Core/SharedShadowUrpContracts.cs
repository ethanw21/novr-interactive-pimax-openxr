using Mono.Cecil;

// Opt-in local assembly contracts only. Shadow sharing injects private fields
// of the render pipeline's sun shadow pass by name and patches five of its
// methods; none of that is visible to the portable doubles.
internal static class SharedShadowUrpContracts
{
    internal static void Run(Action<string, Action> check, string gameManagedDirectory)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Path.Combine(gameManagedDirectory,
            "Unity.RenderPipelines.Universal.Runtime.dll"));
        var module = assembly.MainModule;
        TypeDefinition Type(string name) => module.Types.SingleOrDefault(t => t.FullName == name) ??
            throw new InvalidOperationException(name + " is missing.");
        MethodDefinition Method(TypeDefinition type, string name, params string[] parameters) =>
            type.Methods.SingleOrDefault(m => m.Name == name && m.HasBody &&
                m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters)) ??
            throw new InvalidOperationException($"{type.Name}.{name}({string.Join(", ", parameters)}) is missing.");

        const string pass = "UnityEngine.Rendering.Universal.Internal.MainLightShadowCasterPass";
        const string data = "UnityEngine.Rendering.Universal.RenderingData&";
        check("Local URP metadata: sun shadow pass keeps the fields shadow sharing saves and restores", () =>
        {
            var fields = Type(pass).Fields.Where(f => !f.IsStatic).ToDictionary(f => f.Name, f => f.FieldType.FullName);
            foreach (var (name, type) in new[]
                     {
                         ("m_CascadeBorder", "System.Single"), ("m_MaxShadowDistanceSq", "System.Single"),
                         ("m_ShadowCasterCascadesCount", "System.Int32"), ("m_CreateEmptyShadowmap", "System.Boolean"),
                         ("renderTargetWidth", "System.Int32"), ("renderTargetHeight", "System.Int32"),
                         ("m_MainLightShadowmapTexture", "UnityEngine.Rendering.RTHandle"),
                         ("m_CascadeSlices", "UnityEngine.Rendering.Universal.ShadowSliceData[]"),
                         ("m_CascadeSplitDistances", "UnityEngine.Vector4[]")
                     })
                Require(fields.TryGetValue(name, out var actual) && actual == type,
                    $"MainLightShadowCasterPass.{name} is missing or is no longer {type}.");
        });
        check("Local URP metadata: sun shadow pass entry points match the shadow sharing hooks", () =>
        {
            var type = Type(pass);
            Require(Method(type, "Setup", data).ReturnType.FullName == "System.Boolean" &&
                    Method(type, "Setup", data).Parameters[0].Name == "renderingData",
                "MainLightShadowCasterPass.Setup no longer returns whether the pass runs.");
            Method(type, "Configure", "UnityEngine.Rendering.CommandBuffer", "UnityEngine.RenderTextureDescriptor");
            Method(type, "Execute", "UnityEngine.Rendering.ScriptableRenderContext", data);
            // The pass must still set its per-eye shader state itself, with the
            // slice draw as the only part shadow sharing leaves out.
            var draws = Method(type, "RenderMainLightCascadeShadowmap", "UnityEngine.Rendering.ScriptableRenderContext&", data)
                .Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
            Require(draws.Any(m => m.Name == "RenderShadowSlice" && m.Parameters.Count == 6) &&
                    draws.Any(m => m.Name == "SetupMainLightShadowReceiverConstants"),
                "The sun shadow pass no longer draws through ShadowUtils.RenderShadowSlice before setting receiver constants.");
            Require(type.Methods.Single(m => m.Name == "Setup").Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                    .Any(m => m.Name == "ShadowRTReAllocateIfNeeded"),
                "MainLightShadowCasterPass.Setup no longer owns the shadow map allocation.");
        });
        check("Local URP metadata: shadow slice draw and culling setup keep their signatures", () =>
        {
            var slice = Method(Type("UnityEngine.Rendering.Universal.ShadowUtils"), "RenderShadowSlice",
                "UnityEngine.Rendering.CommandBuffer", "UnityEngine.Rendering.ScriptableRenderContext&",
                "UnityEngine.Rendering.Universal.ShadowSliceData&", "UnityEngine.Rendering.ShadowDrawingSettings&",
                "UnityEngine.Matrix4x4", "UnityEngine.Matrix4x4");
            Require(slice.IsStatic && slice.ReturnType.FullName == "System.Void", "ShadowUtils.RenderShadowSlice is no longer a static draw call.");
            var culling = Method(Type("UnityEngine.Rendering.Universal.UniversalRenderer"), "SetupCullingParameters",
                "UnityEngine.Rendering.ScriptableCullingParameters&", "UnityEngine.Rendering.Universal.CameraData&");
            Require(!culling.IsStatic && culling.Parameters[0].Name == "cullingParameters" && culling.Parameters[1].Name == "cameraData",
                "UniversalRenderer.SetupCullingParameters no longer matches the shadow sharing postfix.");
        });
        check("Local URP metadata: a pass without a target of its own is left unbound before the prepasses", () =>
        {
            var renderPass = Type("UnityEngine.Rendering.Universal.ScriptableRenderPass");
            Require(renderPass.Methods.Any(m => m.Name == "ResetTarget" && m.IsPublic && m.Parameters.Count == 0),
                "ScriptableRenderPass.ResetTarget is no longer public.");
            Require(renderPass.Properties.SingleOrDefault(p => p.Name == "useNativeRenderPass")?.SetMethod is { IsStatic: false } setter &&
                    setter.Parameters.Count == 1,
                "ScriptableRenderPass.useNativeRenderPass can no longer be switched off for a reused pass.");
            var camera = Type("UnityEngine.Rendering.Universal.CameraData");
            Require(camera.Properties.Any(p => p.Name == "xr" && p.GetMethod is { IsPublic: true }) &&
                    camera.Fields.Any(f => f.Name == "maxShadowDistance" && f.IsPublic),
                "CameraData no longer exposes the XR pass and shadow distance.");
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
