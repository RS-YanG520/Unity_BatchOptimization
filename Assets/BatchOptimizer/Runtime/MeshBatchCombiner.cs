using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace BatchOptimizer
{
    /// <summary>一个合批分组：网格 + 材质数组（+ 光照贴图索引）完全相同的对象集合。</summary>
    public class BatchGroup
    {
        /// <summary>分组键，用于日志与调试。</summary>
        public string key;

        /// <summary>该组共享的网格。</summary>
        public Mesh sharedMesh;

        /// <summary>该组共享的材质数组（顺序敏感）。</summary>
        public Material[] sharedMaterials;

        /// <summary>组内所有 Renderer。</summary>
        public List<Renderer> renderers = new List<Renderer>();

        public int SubMeshCount => sharedMesh != null ? sharedMesh.subMeshCount : 1;

        /// <summary>合批前的 Draw Call 数（每个 Renderer 的每个子网格算一次）。</summary>
        public int OriginalDrawCalls => renderers.Count * SubMeshCount;

        public override string ToString() => key;
    }

    /// <summary>一次合并产出的结果。</summary>
    public class CombineResult
    {
        public BatchGroup group;
        public Mesh combinedMesh;
        public Material[] materials;
        public List<Renderer> sourceRenderers = new List<Renderer>();
        public int vertexCount;
        public bool preserveLightmap;
    }

    /// <summary>
    /// 合批核心逻辑（编辑器与运行时共用）。
    /// 关键点：按「网格 + 材质数组 + 光照贴图索引」精确分组，只把完全相同的模型合并到一起，
    /// 从而避免 Unity 内置静态合批把不同模型 / 不同材质错误合并、导致 Draw Call 反而升高的问题。
    /// </summary>
    public static class MeshBatchCombiner
    {
        /// <summary>
        /// 扫描并按 (Mesh + 材质数组 + 光照贴图索引) 分组。
        /// 注意：调用方负责传入已经过“包含过滤”的候选 Renderer 列表。
        /// </summary>
        public static List<BatchGroup> ScanAndGroup(IEnumerable<Renderer> candidates, bool groupByLightmap, int minGroupCount = 2)
        {
            var groups = new Dictionary<string, BatchGroup>();
            int valid = 0;
            int skipped = 0;

            foreach (Renderer r in candidates)
            {
                if (r == null) continue;

                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null)
                {
                    skipped++;
                    BatchLogger.DebugLog("跳过「" + r.name + "」：缺少 MeshFilter 或 sharedMesh");
                    continue;
                }

                Mesh mesh = mf.sharedMesh;
                Material[] mats = r.sharedMaterials;

                if (mesh.subMeshCount <= 0 || mesh.vertexCount <= 0)
                {
                    skipped++;
                    BatchLogger.DebugLog("跳过「" + r.name + "」：网格为空（subMesh=" + mesh.subMeshCount + ", vertex=" + mesh.vertexCount + "）");
                    continue;
                }

                int matCount = mats != null ? mats.Length : 0;
                if (matCount != mesh.subMeshCount)
                {
                    skipped++;
                    BatchLogger.Warn("跳过「" + r.name + "」：材质数量 " + matCount + " 与子网格数量 " + mesh.subMeshCount + " 不一致，无法正确合批");
                    continue;
                }

                string key = BuildGroupKey(mesh, mats, groupByLightmap ? r.lightmapIndex : -1, groupByLightmap ? r.realtimeLightmapIndex : -1);

                if (!groups.TryGetValue(key, out BatchGroup g))
                {
                    g = new BatchGroup
                    {
                        key = key,
                        sharedMesh = mesh,
                        sharedMaterials = (Material[])mats.Clone(),
                    };
                    groups[key] = g;
                }
                g.renderers.Add(r);
                valid++;
            }

            var result = new List<BatchGroup>();
            foreach (var g in groups.Values)
            {
                if (g.renderers.Count < minGroupCount)
                {
                    BatchLogger.DebugLog("分组「" + g.sharedMesh.name + "」仅 " + g.renderers.Count + " 个对象，少于 " + minGroupCount + "，不合并");
                    continue;
                }
                result.Add(g);
            }

            BatchLogger.Info("扫描完成：有效 " + valid + " / 跳过 " + skipped + " / 分组 " + groups.Count + " / 可合批 " + result.Count);
            return result;
        }

        /// <summary>把一个分组合并成一个或多个网格（自动按顶点上限分块）。</summary>
        public static List<CombineResult> CombineGroup(BatchGroup group, Transform parent, int maxVerticesPerMesh, bool preserveLightmap)
        {
            var results = new List<CombineResult>();

            foreach (List<Renderer> chunk in ChunkByVertexLimit(group.renderers, maxVerticesPerMesh))
            {
                Mesh combined = BuildCombinedMesh(chunk, parent, preserveLightmap);
                if (combined == null) continue;

                results.Add(new CombineResult
                {
                    group = group,
                    combinedMesh = combined,
                    materials = group.sharedMaterials,
                    sourceRenderers = chunk,
                    vertexCount = combined.vertexCount,
                    preserveLightmap = preserveLightmap,
                });
            }
            return results;
        }

        /// <summary>估算一个分组最终会生成多少个合并网格（按顶点上限分块）。</summary>
        public static int EstimateCombinedMeshCount(BatchGroup group, int maxVerticesPerMesh)
        {
            return ChunkByVertexLimit(group.renderers, maxVerticesPerMesh).Count;
        }

        /// <summary>
        /// 根据合并结果创建新的 GameObject（MeshFilter + MeshRenderer），并禁用原始 Renderer。
        /// 编辑器与运行时均可调用。
        /// </summary>
        public static GameObject CreateCombinedObject(CombineResult result, Transform parent, bool disableWholeObject)
        {
            GameObject go = new GameObject(result.combinedMesh.name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = result.combinedMesh;

            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = result.materials;

            Renderer src = result.sourceRenderers[0];
            mr.shadowCastingMode = src.shadowCastingMode;
            mr.receiveShadows = src.receiveShadows;
            mr.lightProbeUsage = src.lightProbeUsage;
            mr.reflectionProbeUsage = src.reflectionProbeUsage;
            mr.probeAnchor = src.probeAnchor;
            mr.staticShadowCaster = src.staticShadowCaster;
            go.layer = src.gameObject.layer;

            if (result.preserveLightmap)
            {
                // 合并时已把每个对象的 uv2 换算到光照贴图图集空间，因此合并后使用单位 scaleOffset 即可。
                mr.lightmapIndex = src.lightmapIndex;
                mr.lightmapScaleOffset = new Vector4(1f, 1f, 0f, 0f);
                mr.realtimeLightmapIndex = src.realtimeLightmapIndex;
                mr.realtimeLightmapScaleOffset = new Vector4(1f, 1f, 0f, 0f);
            }
            else
            {
                mr.lightmapIndex = -1;
                mr.realtimeLightmapIndex = -1;
            }

            // 只有「没有光照贴图、且依赖光照探针」的对象，合并后探针采样位置才会失真；
            // 有光照贴图（Bakery）的对象运行时用光照贴图采样，不受影响，不再误报。
            // 这里降级为 DebugLog，避免每次合并都刷一条警告；调用方会汇总一次提示。
            if (src.lightmapIndex < 0 && src.lightProbeUsage != LightProbeUsage.Off)
                BatchLogger.DebugLog("合并对象「" + go.name + "」依赖光照探针（无光照贴图），合并后探针采样位置可能与原位置不一致");

            // 附加撤销标记
            var marker = go.AddComponent<BatchCombinedResult>();
            marker.sourceRenderers = new List<Renderer>(result.sourceRenderers);
            marker.drawCallsBefore = result.sourceRenderers.Count * result.sourceRenderers[0].GetComponent<MeshFilter>().sharedMesh.subMeshCount;
            marker.drawCallsAfter = result.materials.Length;

            // 禁用原始对象
            foreach (Renderer r in result.sourceRenderers)
            {
                if (disableWholeObject)
                    r.gameObject.SetActive(false);
                else
                    r.enabled = false;
            }

            return go;
        }

        // ---------- 内部实现 ----------

        private static string BuildGroupKey(Mesh mesh, Material[] mats, int lightmapIndex, int realtimeLightmapIndex)
        {
            var sb = new StringBuilder();
            sb.Append(mesh.name).Append('#').Append(mesh.GetInstanceID());
            sb.Append("|lm:").Append(lightmapIndex);
            sb.Append("|rlm:").Append(realtimeLightmapIndex);
            sb.Append("|mats:");
            if (mats == null || mats.Length == 0)
            {
                sb.Append("<none>");
            }
            else
            {
                for (int i = 0; i < mats.Length; i++)
                {
                    Material m = mats[i];
                    sb.Append(m != null ? m.name + "#" + m.GetInstanceID() : "<null>");
                    if (i < mats.Length - 1) sb.Append(',');
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 估算一个 Renderer 合并后贡献的顶点数。
        /// 多子网格合并时顶点可能因子网格共享而膨胀，因此按 subMeshCount 保守估算。
        /// </summary>
        private static int EstimateVertices(Renderer r)
        {
            Mesh m = r.GetComponent<MeshFilter>().sharedMesh;
            return m.vertexCount * Mathf.Max(1, m.subMeshCount);
        }

        private static List<List<Renderer>> ChunkByVertexLimit(List<Renderer> renderers, int maxVerticesPerMesh)
        {
            var chunks = new List<List<Renderer>>();
            List<Renderer> current = null;
            int currentVerts = 0;

            foreach (Renderer r in renderers)
            {
                int verts = EstimateVertices(r);

                if (current == null || (current.Count > 0 && currentVerts + verts > maxVerticesPerMesh))
                {
                    current = new List<Renderer>();
                    chunks.Add(current);
                    currentVerts = 0;
                }
                current.Add(r);
                currentVerts += verts;
            }

            // 过滤掉只有 1 个对象的块：合并单对象没有 draw call 收益，只会白白复制网格并禁用原对象。
            chunks.RemoveAll(c => c.Count < 2);
            return chunks;
        }

        private static Mesh BuildCombinedMesh(List<Renderer> renderers, Transform parent, bool preserveLightmap)
        {
            Mesh firstMesh = renderers[0].GetComponent<MeshFilter>().sharedMesh;
            int subMeshCount = firstMesh.subMeshCount;

            foreach (Renderer r in renderers)
            {
                int sc = r.GetComponent<MeshFilter>().sharedMesh.subMeshCount;
                if (sc != subMeshCount)
                {
                    BatchLogger.Warn("组内子网格数量不一致：「" + r.name + "」为 " + sc + "，首对象为 " + subMeshCount + "，跳过该分组");
                    return null;
                }
            }

            var combine = new CombineInstance[renderers.Count * subMeshCount];
            Matrix4x4 parentWorldToLocal = parent != null ? parent.worldToLocalMatrix : Matrix4x4.identity;

            int idx = 0;
            foreach (Renderer r in renderers)
            {
                Matrix4x4 local = parentWorldToLocal * r.transform.localToWorldMatrix;
                for (int s = 0; s < subMeshCount; s++)
                {
                    combine[idx].mesh = r.GetComponent<MeshFilter>().sharedMesh;
                    combine[idx].subMeshIndex = s;
                    combine[idx].transform = local;
                    if (preserveLightmap)
                    {
                        combine[idx].lightmapScaleOffset = r.lightmapScaleOffset;
                        combine[idx].realtimeLightmapScaleOffset = r.realtimeLightmapScaleOffset;
                    }
                    idx++;
                }
            }

            Mesh result = new Mesh();
            result.name = firstMesh.name + "_Combined";

            // 估算合并后顶点数（保守按子网格数放大）。超过 16 位索引上限就提前用 32 位。
            int totalVerts = 0;
            foreach (Renderer r in renderers) totalVerts += EstimateVertices(r);
            bool useUInt32 = totalVerts > 65535;
            if (useUInt32)
            {
                result.indexFormat = IndexFormat.UInt32;
                BatchLogger.DebugLog("合并网格「" + firstMesh.name + "」估算顶点 " + totalVerts + " 超过 65535，使用 32 位索引");
            }

            try
            {
                result.CombineMeshes(combine, mergeSubMeshes: true, useMatrices: true, hasLightmapData: preserveLightmap);
            }
            catch (Exception e)
            {
                // 16 位索引因顶点过多失败时，改用 32 位索引重试一次
                if (!useUInt32)
                {
                    BatchLogger.Warn("合并「" + firstMesh.name + "」16 位索引失败（" + e.Message + "），改用 32 位索引重试");
                    UnityEngine.Object.DestroyImmediate(result);
                    result = new Mesh();
                    result.name = firstMesh.name + "_Combined";
                    result.indexFormat = IndexFormat.UInt32;
                    try
                    {
                        result.CombineMeshes(combine, mergeSubMeshes: true, useMatrices: true, hasLightmapData: preserveLightmap);
                    }
                    catch (Exception e2)
                    {
                        BatchLogger.Error("合并「" + firstMesh.name + "」失败：" + e2.Message);
                        UnityEngine.Object.DestroyImmediate(result);
                        return null;
                    }
                }
                else
                {
                    BatchLogger.Error("合并「" + firstMesh.name + "」失败：" + e.Message);
                    UnityEngine.Object.DestroyImmediate(result);
                    return null;
                }
            }

            BatchLogger.Info("合并网格成功：「" + firstMesh.name + "」对象数 " + renderers.Count +
                             "，子网格 " + subMeshCount + "，顶点 " + result.vertexCount +
                             "，三角形 " + (result.triangles.Length / 3));
            return result;
        }
    }
}
