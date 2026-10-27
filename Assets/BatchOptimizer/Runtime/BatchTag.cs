using UnityEngine;

namespace BatchOptimizer
{
    /// <summary>
    /// 手动标记一个对象参与合批。
    /// 静态合批窗口与运行时合批器都能识别此标记。
    /// </summary>
    [AddComponentMenu("Batch Optimizer/Batch Tag")]
    [DisallowMultipleComponent]
    public class BatchTag : MonoBehaviour
    {
        [Tooltip("是否参与合批。取消勾选可临时排除该对象。")]
        public bool includeInBatching = true;
    }
}
