using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BatchOptimizer
{
    /// <summary>
    /// 一次性清理工具：删除当前场景中所有挂载的 BatchTag 组件。
    /// 用于切换到「收集静态对象（isStatic）」方案后，清掉之前方案 B 留在模型上的标记。
    /// 使用 Undo 注册，删除后可用 Ctrl+Z 撤销。
    /// </summary>
    public static class BatchTagCleanup
    {
        [MenuItem("Tools/Batch Optimizer/删除场景中的 Batch Tag")]
        public static void RemoveAllBatchTags()
        {
            // 包含 inactive 对象，避免漏掉被禁用的模型
            var tags = Object.FindObjectsByType<BatchTag>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            if (tags == null || tags.Length == 0)
            {
                BatchLogger.Info("场景中没有找到 BatchTag 组件，无需清理");
                EditorUtility.DisplayDialog("清理 Batch Tag", "当前场景中没有找到 BatchTag 组件。", "确定");
                return;
            }

            var names = new List<string>();
            int removed = 0;

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();

            foreach (var tag in tags)
            {
                if (tag == null) continue;
                names.Add(tag.gameObject.name);
                Undo.DestroyObjectImmediate(tag);
                removed++;
            }

            Undo.CollapseUndoOperations(undoGroup);

            if (removed > 0)
            {
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                BatchLogger.Info("已删除 " + removed + " 个 BatchTag 组件（涉及 " + names.Count + " 个对象），可用 Ctrl+Z 撤销");
            }

            EditorUtility.DisplayDialog("清理 Batch Tag",
                "已删除 " + removed + " 个 BatchTag 组件。\n\nCtrl+Z 可撤销本次删除。", "确定");
        }
    }
}
