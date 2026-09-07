using System;
using System.Collections.Generic;

namespace RFramework
{
    /// <summary>
    /// 创建、缓存并统一驱动框架模块。
    /// </summary>
    public static class RFrameworkModuleHost
    {
        private static readonly Dictionary<Type, RFrameworkModule> ModulesByContract =
            new Dictionary<Type, RFrameworkModule>();
        private static readonly List<RFrameworkModule> OrderedModules =
            new List<RFrameworkModule>();

        private static RFrameworkModule[] schedule = Array.Empty<RFrameworkModule>();
        private static bool isStopping;

        /// <summary>
        /// 获取已创建的模块数量。
        /// </summary>
        public static int Count => OrderedModules.Count;

        /// <summary>
        /// 获取指定契约对应的内置模块；尚未创建时通过显式映射创建。
        /// </summary>
        /// <typeparam name="T">模块接口类型。</typeparam>
        /// <returns>模块实例。</returns>
        public static T Get<T>() where T : class
        {
            Type contractType = typeof(T);
            if (!contractType.IsInterface)
            {
                throw new RFrameworkException(
                    $"Module contract '{contractType.FullName}' must be an interface.");
            }

            if (ModulesByContract.TryGetValue(contractType, out RFrameworkModule existing))
            {
                return (T)(object)existing;
            }

            if (isStopping)
            {
                throw new RFrameworkException(
                    $"Module '{contractType.FullName}' cannot be created while modules are stopping.");
            }

            RFrameworkModule created = CreateModule(contractType);
            ModulesByContract.Add(contractType, created);
            OrderedModules.Add(created);
            OrderedModules.Sort(CompareModules);
            schedule = OrderedModules.ToArray();
            return (T)(object)created;
        }

        /// <summary>
        /// 按调度顺序驱动所有已创建模块。
        /// 单个模块失败不会阻止其余模块本帧继续执行。
        /// </summary>
        /// <param name="deltaTime">受时间缩放影响的帧间隔。</param>
        /// <param name="unscaledDeltaTime">不受时间缩放影响的帧间隔。</param>
        public static void Tick(float deltaTime, float unscaledDeltaTime)
        {
            List<Exception> failures = null;
            RFrameworkModule[] currentSchedule = schedule;
            for (int i = currentSchedule.Length - 1; i >= 0; i--)
            {
                try
                {
                    currentSchedule[i].Tick(deltaTime, unscaledDeltaTime);
                }
                catch (Exception ex)
                {
                    (failures ??= new List<Exception>()).Add(ex);
                }
            }

            ThrowIfFailed("tick", failures);
        }

        /// <summary>
        /// 停止并移除所有模块。单个模块失败不会中断其余模块清理。
        /// </summary>
        public static void StopAll()
        {
            if (isStopping)
            {
                return;
            }

            isStopping = true;
            List<Exception> failures = null;
            try
            {
                RFrameworkModule[] currentSchedule = schedule;
                for (int i = 0; i < currentSchedule.Length; i++)
                {
                    try
                    {
                        currentSchedule[i].Stop();
                    }
                    catch (Exception ex)
                    {
                        (failures ??= new List<Exception>()).Add(ex);
                    }
                }
            }
            finally
            {
                ModulesByContract.Clear();
                OrderedModules.Clear();
                schedule = Array.Empty<RFrameworkModule>();
                isStopping = false;
            }

            ThrowIfFailed("stop", failures);
        }

        private static RFrameworkModule CreateModule(Type contractType)
        {
            if (string.IsNullOrEmpty(contractType.Namespace)
                || contractType.Name.Length < 2
                || contractType.Name[0] != 'I')
            {
                throw new RFrameworkException(
                    $"Module contract '{contractType.FullName}' must follow the IXxx naming convention.");
            }

            try
            {
                if (contractType == typeof(IAudioModule)) return new AudioModule();
                if (contractType == typeof(IConfigModule)) return new ConfigModule();
                if (contractType == typeof(IDownloadModule)) return new DownloadModule();
                if (contractType == typeof(IEntityModule)) return new EntityModule();
                if (contractType == typeof(IEventModule)) return new EventModule();
                if (contractType == typeof(IFsmModule)) return new FsmModule();
                if (contractType == typeof(ILocalizationModule)) return new LocalizationModule();
                if (contractType == typeof(INetworkModule)) return new NetworkModule();
                if (contractType == typeof(IPoolModule)) return new PoolModule();
                if (contractType == typeof(IProcedureModule)) return new ProcedureModule();
                if (contractType == typeof(IResourceModule)) return new ResourceModule();
                if (contractType == typeof(ISceneModule)) return new SceneModule();
                if (contractType == typeof(ITimerModule)) return new TimerModule();
                if (contractType == typeof(IUIModule)) return new UIModule();
                if (contractType == typeof(IWebRequestModule)) return new WebRequestModule();
            }
            catch (Exception ex)
            {
                throw new RFrameworkException(
                    $"Module '{contractType.FullName}' could not be created.", ex);
            }

            throw new RFrameworkException(
                $"No valid module implementation was found for '{contractType.FullName}'.");
        }

        private static int CompareModules(RFrameworkModule left, RFrameworkModule right)
        {
            int order = left.Order.CompareTo(right.Order);
            return order != 0
                ? order
                : string.CompareOrdinal(left.GetType().FullName, right.GetType().FullName);
        }

        private static void ThrowIfFailed(string operation, List<Exception> failures)
        {
            if (failures == null)
            {
                return;
            }

            throw new RFrameworkException(
                $"Framework module {operation} completed with {failures.Count} error(s).",
                new AggregateException(failures));
        }
    }
}
