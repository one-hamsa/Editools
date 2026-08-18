using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Re-layouts a mesh into two vertex streams: stream 0 holds only position, stream 1 holds
/// every other attribute. Position-only GPU passes (tiler binning, shadow, depth, motion
/// vectors) then fetch tightly packed 12-byte vertices instead of pulling the full
/// interleaved vertex record through the cache.
/// GreyPrimitive applies this to every rebuilt render mesh; callers generating meshes
/// outside that flow can invoke TrySplit directly.
/// </summary>
public static class PositionStreamSplitter
{
    /// <summary>
    /// Splits the mesh in place, preserving formats, attribute order, indices, submeshes,
    /// and bounds byte-for-byte. Returns false with a reason for meshes it must not touch
    /// (already split, skinned, blend-shaped, or position-only).
    /// </summary>
    public static bool TrySplit(Mesh mesh, out string skipReason)
    {
        skipReason = null;
        var attrs = mesh.GetVertexAttributes();
        if (attrs.All(a => a.attribute != VertexAttribute.Position))
        {
            skipReason = "no position attribute";
            return false;
        }
        if (attrs.Length < 2)
        {
            skipReason = "position is the only attribute";
            return false;
        }
        if (attrs.Any(a => a.stream != 0))
        {
            skipReason = "already multi-stream";
            return false;
        }
        if (mesh.blendShapeCount > 0)
        {
            skipReason = "has blend shapes";
            return false;
        }
        if (mesh.HasVertexAttribute(VertexAttribute.BlendWeight) ||
            mesh.HasVertexAttribute(VertexAttribute.BlendIndices))
        {
            skipReason = "skinned";
            return false;
        }

        int vertexCount = mesh.vertexCount;
        var bounds = mesh.bounds;
        var indexFormat = mesh.indexFormat;

        var newAttrs = new VertexAttributeDescriptor[attrs.Length];
        int posOffset = 0, posSize = 0, offset = 0;
        for (int i = 0; i < attrs.Length; i++)
        {
            newAttrs[i] = attrs[i];
            int size = FormatSize(attrs[i].format) * attrs[i].dimension;
            if (attrs[i].attribute == VertexAttribute.Position)
            {
                newAttrs[i].stream = 0;
                posOffset = offset;
                posSize = size;
            }
            else
            {
                newAttrs[i].stream = 1;
            }
            offset += size;
        }
        int srcStride = offset;
        int restStride = srcStride - posSize;

        var dstArray = Mesh.AllocateWritableMeshData(1);
        var dst = dstArray[0];
        dst.SetVertexBufferParams(vertexCount, newAttrs);

        using (var srcArray = Mesh.AcquireReadOnlyMeshData(mesh))
        {
            var src = srcArray[0];
            var srcBytes = src.GetVertexData<byte>(0);
            var dstPos = dst.GetVertexData<byte>(0);
            var dstRest = dst.GetVertexData<byte>(1);

            // rest keeps declaration order, so source bytes around position map 1:1
            int afterSize = srcStride - posOffset - posSize;
            for (int v = 0; v < vertexCount; v++)
            {
                int s = v * srcStride;
                NativeArray<byte>.Copy(srcBytes, s + posOffset, dstPos, v * posSize, posSize);
                if (posOffset > 0)
                    NativeArray<byte>.Copy(srcBytes, s, dstRest, v * restStride, posOffset);
                if (afterSize > 0)
                    NativeArray<byte>.Copy(srcBytes, s + posOffset + posSize, dstRest, v * restStride + posOffset, afterSize);
            }

            var srcIndices = src.GetIndexData<byte>();
            int indexCount = srcIndices.Length / (indexFormat == IndexFormat.UInt16 ? 2 : 4);
            dst.SetIndexBufferParams(indexCount, indexFormat);
            var dstIndices = dst.GetIndexData<byte>();
            srcIndices.CopyTo(dstIndices);

            dst.subMeshCount = mesh.subMeshCount;
            const MeshUpdateFlags flags = MeshUpdateFlags.DontRecalculateBounds |
                                          MeshUpdateFlags.DontValidateIndices |
                                          MeshUpdateFlags.DontNotifyMeshUsers;
            for (int i = 0; i < mesh.subMeshCount; i++)
                dst.SetSubMesh(i, mesh.GetSubMesh(i), flags);
        }

        Mesh.ApplyAndDisposeWritableMeshData(dstArray, mesh,
            MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices |
            MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds);
        mesh.bounds = bounds;
        return true;
    }

    static int FormatSize(VertexAttributeFormat format) => format switch
    {
        VertexAttributeFormat.Float32 or VertexAttributeFormat.UInt32 or VertexAttributeFormat.SInt32 => 4,
        VertexAttributeFormat.Float16 or VertexAttributeFormat.UNorm16 or VertexAttributeFormat.SNorm16 or
        VertexAttributeFormat.UInt16 or VertexAttributeFormat.SInt16 => 2,
        _ => 1,
    };
}
