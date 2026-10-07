// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Xunit;

namespace Despada.Tests;

/// <summary>
/// MarseyLoader calls Assembly.GetTypes() on Despada.dll before any Despada code runs, i.e. before
/// the embedded ImGui.NET can be resolved. A field whose layout needs an ImGui.NET value type
/// (ImGuiKey, ImDrawListPtr, Nullable&lt;ImGuiCol&gt;…) makes that type fail to load. Checked on
/// the metadata, so it doesn't matter whether ImGui.NET happens to be resolvable in the test run.
/// </summary>
public class LoadTests
{
    private static string DespadaPath => typeof(Despada.Core.Feature).Assembly.Location;

    [Fact]
    public void NoFieldNeedsAnImGuiValueType()
    {
        using var pe = new PEReader(File.OpenRead(DespadaPath));
        var md = pe.GetMetadataReader();
        var provider = new LayoutProvider();
        var bad = new List<string>();

        foreach (var th in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(th);
            foreach (var fh in type.GetFields())
            {
                var field = md.GetFieldDefinition(fh);
                if (field.DecodeSignature(provider, null).NeedsImGui)
                    bad.Add($"{FullName(md, type)}.{md.GetString(field.Name)}");
            }

            // Base types and implemented interfaces are loaded with the type: any ImGui.NET type in
            // them (IComparer<ImGuiKey>, a struct base…) breaks it too.
            if (!type.BaseType.IsNil && Mentions(md, type.BaseType))
                bad.Add($"{FullName(md, type)} (base type)");
            foreach (var ih in type.GetInterfaceImplementations())
                if (Mentions(md, md.GetInterfaceImplementation(ih).Interface))
                    bad.Add($"{FullName(md, type)} (interface)");
        }

        Assert.True(bad.Count == 0, "Fields that need ImGui.NET at type-load time:\n" + string.Join("\n", bad));
    }

    /// <summary>
    /// The real thing: Despada.dll in a fresh load context where ImGui.NET can't be found, and no
    /// Despada code runs first (so the embedded-assembly resolver isn't installed), like MarseyLoader.
    /// </summary>
    [Fact]
    public void GetTypesSucceedsWithoutImGui()
    {
        var alc = new NoImGuiContext();
        try
        {
            var asm = alc.LoadFromAssemblyPath(DespadaPath);
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var failed = ex.Types.Select((t, i) => (t, i)).Where(x => x.t is null).Select(x => ex.LoaderExceptions[x.i]?.Message);
                throw new Xunit.Sdk.XunitException("GetTypes failed:\n" + string.Join("\n", failed.Distinct()));
            }
            Assert.Contains(types, t => t.FullName == "MarseyEntry");
            Assert.DoesNotContain(alc.Assemblies, a => a.GetName().Name == "ImGui.NET");
        }
        finally
        {
            alc.Unload();
        }
    }

    private sealed class NoImGuiContext() : AssemblyLoadContext("no-imgui", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
            => name.Name == "ImGui.NET" ? throw new FileNotFoundException("ImGui.NET is not available at load time") : null;
    }

    private static string FullName(MetadataReader md, TypeDefinition type)
    {
        var name = md.GetString(type.Name);
        var outer = type.GetDeclaringType();
        if (!outer.IsNil) return FullName(md, md.GetTypeDefinition(outer)) + "+" + name;
        var ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    // Does this type reference/spec mention anything from ImGui.NET (itself or in generic arguments)?
    private static bool Mentions(MetadataReader md, EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeReference     => LayoutProvider.FromImGui(md, (TypeReferenceHandle)handle),
        HandleKind.TypeSpecification => md.GetTypeSpecification((TypeSpecificationHandle)handle)
                                          .DecodeSignature(new MentionProvider(), null),
        _                            => false,
    };

    private sealed class MentionProvider : ISignatureTypeProvider<bool, object?>
    {
        public bool GetPrimitiveType(PrimitiveTypeCode typeCode) => false;
        public bool GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => false;
        public bool GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => LayoutProvider.FromImGui(reader, handle);
        public bool GetGenericInstantiation(bool genericType, ImmutableArray<bool> typeArguments)
            => genericType || typeArguments.Any(a => a);
        public bool GetSZArrayType(bool elementType) => elementType;
        public bool GetArrayType(bool elementType, ArrayShape shape) => elementType;
        public bool GetPointerType(bool elementType) => elementType;
        public bool GetByReferenceType(bool elementType) => elementType;
        public bool GetFunctionPointerType(MethodSignature<bool> signature) => false;
        public bool GetGenericMethodParameter(object? genericContext, int index) => false;
        public bool GetGenericTypeParameter(object? genericContext, int index) => false;
        public bool GetModifiedType(bool modifier, bool unmodifiedType, bool isRequired) => unmodifiedType;
        public bool GetPinnedType(bool elementType) => elementType;
        public bool GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }

    // A field's type as far as type layout is concerned: is it a value type, and does laying it out
    // need an ImGui.NET value type (itself, or a generic argument of a value type)?
    private readonly record struct Shape(bool IsValueType, bool NeedsImGui);

    private sealed class LayoutProvider : ISignatureTypeProvider<Shape, object?>
    {
        private const byte ValueTypeKind = 0x11;

        public Shape GetPrimitiveType(PrimitiveTypeCode typeCode) => new(true, false);

        public Shape GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => new(rawTypeKind == ValueTypeKind, false);

        public Shape GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var isValue = rawTypeKind == ValueTypeKind;
            return new(isValue, isValue && FromImGui(reader, handle));
        }

        public static bool FromImGui(MetadataReader reader, TypeReferenceHandle handle)
        {
            var scope = reader.GetTypeReference(handle).ResolutionScope;
            while (scope.Kind == HandleKind.TypeReference) // nested type: walk to the outermost
                scope = reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
            return scope.Kind == HandleKind.AssemblyReference &&
                   reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name) == "ImGui.NET";
        }

        public Shape GetGenericInstantiation(Shape genericType, ImmutableArray<Shape> typeArguments)
            => new(genericType.IsValueType,
                   genericType.IsValueType && (genericType.NeedsImGui || typeArguments.Any(a => a.NeedsImGui)));

        // References and pointers don't need the element type's layout.
        public Shape GetSZArrayType(Shape elementType) => new(false, false);
        public Shape GetArrayType(Shape elementType, ArrayShape shape) => new(false, false);
        public Shape GetPointerType(Shape elementType) => new(true, false);
        public Shape GetByReferenceType(Shape elementType) => new(false, false);
        public Shape GetFunctionPointerType(MethodSignature<Shape> signature) => new(true, false);
        public Shape GetGenericMethodParameter(object? genericContext, int index) => new(false, false);
        public Shape GetGenericTypeParameter(object? genericContext, int index) => new(false, false);
        public Shape GetModifiedType(Shape modifier, Shape unmodifiedType, bool isRequired) => unmodifiedType;
        public Shape GetPinnedType(Shape elementType) => elementType;
        public Shape GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}
