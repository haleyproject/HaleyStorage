using Haley.Abstractions;
using Haley.Enums;
using Haley.Models;
using Haley.Utils;

namespace Haley.Services {
    public partial class StorageCoordinator {
        volatile WritePolicySnapshot _writePolicy = WritePolicySnapshot.Empty;
        readonly object _writePolicySync = new();

        /// <summary>
        /// Checks the process-wide write switch and the module/workspace restrictions loaded from OSSSource.
        /// </summary>
        public IFeedback CheckWriteAccess(IVaultReadRequest request) {
            var result = new Feedback();
            if (!WriteMode)
                return result.SetMessage("Application is in Read-Only mode.");
            if (request == null)
                return result.SetMessage("Write access cannot be evaluated without a request scope.");
            if (request.ReadOnlyMode)
                return result.SetMessage("Request is in Read-Only mode.");
            if (request.Scope?.Client == null || request.Scope?.Module == null)
                return result.SetMessage("Client and module information are required to evaluate write access.");

            var moduleCuid = StorageUtils.GenerateCuid(request, VaultObjectType.Module);
            var workspaceCuid = request.Scope.Workspace == null
                ? null
                : StorageUtils.GenerateCuid(request, VaultObjectType.WorkSpace);

            return CheckWriteAccess(moduleCuid, workspaceCuid, ScopeLabel(request));
        }

        public StorageWritePolicyStatus GetWritePolicy(IVaultReadRequest request) {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Scope?.Client == null || request.Scope?.Module == null)
                throw new ArgumentException("Client and module information are required to evaluate write policy.", nameof(request));

            var policy = _writePolicy;
            var moduleCuid = StorageUtils.GenerateCuid(request, VaultObjectType.Module);
            var workspaceCuid = request.Scope.Workspace == null
                ? null
                : StorageUtils.GenerateCuid(request, VaultObjectType.WorkSpace);
            var hasModuleWrite = policy.ModuleWrites.TryGetValue(moduleCuid, out var moduleWrite);
            var workspaceWrite = false;
            var hasWorkspaceWrite = !string.IsNullOrWhiteSpace(workspaceCuid)
                && policy.WorkspaceWrites.TryGetValue(workspaceCuid, out workspaceWrite);
            bool? configuredWrite = request.Scope.Workspace == null
                ? hasModuleWrite ? moduleWrite : null
                : hasWorkspaceWrite ? workspaceWrite : null;

            return new StorageWritePolicyStatus {
                Client = request.Scope.Client.DisplayName ?? request.Scope.Client.Name,
                Module = request.Scope.Module.DisplayName ?? request.Scope.Module.Name,
                Workspace = request.Scope.Workspace?.DisplayName ?? request.Scope.Workspace?.Name,
                Level = request.Scope.Workspace == null ? "module" : "workspace",
                GlobalWriteEnabled = WriteMode,
                ConfiguredWrite = configuredWrite,
                ModuleWrite = hasModuleWrite ? moduleWrite : null,
                WorkspaceWrite = hasWorkspaceWrite ? workspaceWrite : null,
                EffectiveWrite = WriteMode
                    && !request.ReadOnlyMode
                    && (!hasModuleWrite || moduleWrite)
                    && (!hasWorkspaceWrite || workspaceWrite)
            };
        }

        public StorageWritePolicyStatus SetWritePolicy(IVaultReadRequest request, bool? write) {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Scope?.Client == null || request.Scope?.Module == null)
                throw new ArgumentException("Client and module information are required to change write policy.", nameof(request));

            var moduleCuid = StorageUtils.GenerateCuid(request, VaultObjectType.Module);
            var workspaceCuid = request.Scope.Workspace == null
                ? null
                : StorageUtils.GenerateCuid(request, VaultObjectType.WorkSpace);

            lock (_writePolicySync) {
                var current = _writePolicy;
                var modules = new Dictionary<string, bool>(current.ModuleWrites, StringComparer.OrdinalIgnoreCase);
                var workspaces = new Dictionary<string, bool>(current.WorkspaceWrites, StringComparer.OrdinalIgnoreCase);
                var workspaceModules = new Dictionary<string, string>(current.WorkspaceModules, StringComparer.OrdinalIgnoreCase);

                if (string.IsNullOrWhiteSpace(workspaceCuid)) {
                    if (write.HasValue) modules[moduleCuid] = write.Value;
                    else modules.Remove(moduleCuid);
                } else {
                    if (write.HasValue) {
                        workspaces[workspaceCuid] = write.Value;
                        workspaceModules[workspaceCuid] = moduleCuid;
                    } else {
                        workspaces.Remove(workspaceCuid);
                        workspaceModules.Remove(workspaceCuid);
                    }
                }

                _writePolicy = CreateWritePolicySnapshot(modules, workspaces, workspaceModules);
            }

            return GetWritePolicy(request);
        }

        internal bool IsWriteAllowed(string client, string module, string workspace = null) {
            if (!WriteMode || string.IsNullOrWhiteSpace(client) || string.IsNullOrWhiteSpace(module))
                return false;

            var normalizedClient = client.ToDBName();
            var normalizedModule = module.ToDBName();
            var moduleCuid = StorageUtils.GenerateCuid(normalizedClient, normalizedModule);
            var workspaceCuid = string.IsNullOrWhiteSpace(workspace)
                ? null
                : StorageUtils.GenerateCuid(normalizedClient, normalizedModule, workspace.ToDBName());
            return CheckWriteAccess(moduleCuid, workspaceCuid, $"{client}/{module}/{workspace}").Status;
        }

        IFeedback CheckModuleWideWriteAccess(IVaultReadRequest request) {
            var scoped = CheckWriteAccess(request);
            if (!scoped.Status) return scoped;

            var moduleCuid = StorageUtils.GenerateCuid(request, VaultObjectType.Module);
            if (_writePolicy.ModulesWithReadOnlyWorkspaces.Contains(moduleCuid))
                return new Feedback(false,
                    $"Module-wide mutation is unavailable because module '{ModuleLabel(ScopeLabel(request))}' contains one or more read-only workspaces.");
            return scoped;
        }

        IFeedback CheckWorkspaceWriteAccess(IVaultReadRequest request, long workspaceId) {
            var scoped = CheckWriteAccess(request);
            if (!scoped.Status) return scoped;
            if (Indexer == null)
                return new Feedback(false, "Unable to resolve the target workspace before applying its write policy.");

            var moduleCuid = StorageUtils.GenerateCuid(request, VaultObjectType.Module);
            var workspace = Indexer.GetAllComponents<VaultWorkSpace>()
                .FirstOrDefault(item => item.Id == workspaceId);
            if (workspace == null)
                return new Feedback(false, "Unable to resolve the target workspace before applying its write policy.");
            if (!string.Equals(workspace.Module?.Cuid.ToString("N"), moduleCuid, StringComparison.OrdinalIgnoreCase))
                return new Feedback(false, "The target workspace does not belong to the requested module.");

            return CheckWriteAccess(moduleCuid, workspace.Cuid.ToString("N"), ScopeLabel(request, workspace.DisplayName));
        }

        void ReplaceWritePolicies(IEnumerable<DSSRegInfo> sources) {
            var modules = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var workspaces = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var workspaceModules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in sources ?? Enumerable.Empty<DSSRegInfo>()) {
                if (source == null
                    || string.IsNullOrWhiteSpace(source.Client)
                    || string.IsNullOrWhiteSpace(source.Module))
                    continue;

                var client = source.Client.ToDBName();
                var module = source.Module.ToDBName();
                var moduleCuid = StorageUtils.GenerateCuid(client, module);
                if (string.IsNullOrWhiteSpace(source.Workspace)) {
                    if (source.Write.HasValue) modules[moduleCuid] = source.Write.Value;
                    else modules.Remove(moduleCuid);
                    continue;
                }

                var workspaceCuid = StorageUtils.GenerateCuid(client, module, source.Workspace.ToDBName());
                if (source.Write.HasValue) {
                    workspaces[workspaceCuid] = source.Write.Value;
                    workspaceModules[workspaceCuid] = moduleCuid;
                } else {
                    workspaces.Remove(workspaceCuid);
                    workspaceModules.Remove(workspaceCuid);
                }
            }

            lock (_writePolicySync)
                _writePolicy = CreateWritePolicySnapshot(modules, workspaces, workspaceModules);
        }

        public async Task<IFeedback> CheckTargetWriteAccessAsync(
            IVaultReadRequest request,
            long? versionId = null,
            string versionCuid = null,
            string documentCuid = null) {

            if (Indexer != null) {
                try { await Indexer.DemandDirectoryAccess(request, versionId, versionCuid, documentCuid); }
                catch (Exception ex) { return new Feedback(false, ex.Message); }
            }
            var scoped = CheckWriteAccess(request);
            var policy = _writePolicy;
            if (!scoped.Status || policy.WorkspaceWrites.Count == 0 || Indexer == null)
                return scoped;
            if (!versionId.HasValue
                && string.IsNullOrWhiteSpace(versionCuid)
                && string.IsNullOrWhiteSpace(documentCuid))
                return scoped;

            var moduleCuid = StorageUtils.GenerateCuid(request, VaultObjectType.Module);
            if (!policy.ModulesWithReadOnlyWorkspaces.Contains(moduleCuid))
                return scoped;

            var workspaceId = await Indexer.GetTargetWorkspaceId(moduleCuid, versionId, versionCuid, documentCuid);
            if (workspaceId < 1)
                return new Feedback(false, "Unable to determine the target workspace before applying its write policy.");

            var workspace = Indexer.GetAllComponents<VaultWorkSpace>()
                .FirstOrDefault(item => item.Id == workspaceId);
            if (workspace == null)
                return new Feedback(false, "Unable to resolve the target workspace before applying its write policy.");

            return CheckWriteAccess(moduleCuid, workspace.Cuid.ToString("N"), ScopeLabel(request, workspace.DisplayName));
        }

        async Task<IFeedback> CheckTargetWriteAccessAsync(
            string moduleCuid,
            string workspaceCuid,
            long versionId) {

            var scoped = CheckWriteAccess(moduleCuid, workspaceCuid, $"{moduleCuid}/{workspaceCuid}");
            if (!scoped.Status || !_writePolicy.ModulesWithReadOnlyWorkspaces.Contains(moduleCuid))
                return scoped;
            if (!string.IsNullOrWhiteSpace(workspaceCuid))
                return scoped;
            if (Indexer == null)
                return new Feedback(false, "Unable to determine the chunk session workspace before applying its write policy.");

            var workspaceId = await Indexer.GetTargetWorkspaceId(moduleCuid, versionId: versionId);
            var workspace = Indexer.GetAllComponents<VaultWorkSpace>()
                .FirstOrDefault(item => item.Id == workspaceId);
            if (workspace == null)
                return new Feedback(false, "Unable to resolve the chunk session workspace before applying its write policy.");

            return CheckWriteAccess(moduleCuid, workspace.Cuid.ToString("N"), workspace.DisplayName);
        }

        IFeedback CheckWriteAccess(string moduleCuid, string workspaceCuid, string scopeLabel) {
            var result = new Feedback();
            if (!WriteMode)
                return result.SetMessage("Application is in Read-Only mode.");
            var policy = _writePolicy;
            if (!string.IsNullOrWhiteSpace(moduleCuid)
                && policy.ModuleWrites.TryGetValue(moduleCuid, out var moduleWrite)
                && !moduleWrite)
                return result.SetMessage($"Module '{ModuleLabel(scopeLabel)}' is configured as read-only by Seed:OSSSource.");
            if (!string.IsNullOrWhiteSpace(workspaceCuid)
                && policy.WorkspaceWrites.TryGetValue(workspaceCuid, out var workspaceWrite)
                && !workspaceWrite)
                return result.SetMessage($"Workspace '{scopeLabel}' is configured as read-only by Seed:OSSSource.");
            return result.SetStatus(true).SetMessage("Write access is allowed.");
        }

        static WritePolicySnapshot CreateWritePolicySnapshot(
            IReadOnlyDictionary<string, bool> modules,
            IReadOnlyDictionary<string, bool> workspaces,
            IReadOnlyDictionary<string, string> workspaceModules) {

            var modulesWithReadOnlyWorkspaces = workspaces
                .Where(item => !item.Value && workspaceModules.ContainsKey(item.Key))
                .Select(item => workspaceModules[item.Key])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new WritePolicySnapshot(modules, workspaces, workspaceModules, modulesWithReadOnlyWorkspaces);
        }

        static string ScopeLabel(IVaultReadRequest request, string workspace = null) {
            var client = request?.Scope?.Client?.DisplayName ?? request?.Scope?.Client?.Name ?? "unknown";
            var module = request?.Scope?.Module?.DisplayName ?? request?.Scope?.Module?.Name ?? "unknown";
            var resolvedWorkspace = workspace
                ?? request?.Scope?.Workspace?.DisplayName
                ?? request?.Scope?.Workspace?.Name;
            return string.IsNullOrWhiteSpace(resolvedWorkspace)
                ? $"{client}/{module}"
                : $"{client}/{module}/{resolvedWorkspace}";
        }

        static string ModuleLabel(string scopeLabel) {
            if (string.IsNullOrWhiteSpace(scopeLabel)) return "unknown";
            var parts = scopeLabel.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length < 2 ? scopeLabel : $"{parts[0]}/{parts[1]}";
        }

        sealed class WritePolicySnapshot(
            IReadOnlyDictionary<string, bool> moduleWrites,
            IReadOnlyDictionary<string, bool> workspaceWrites,
            IReadOnlyDictionary<string, string> workspaceModules,
            IReadOnlySet<string> modulesWithReadOnlyWorkspaces) {

            public static WritePolicySnapshot Empty { get; } = new(
                new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            public IReadOnlyDictionary<string, bool> ModuleWrites { get; } = moduleWrites;
            public IReadOnlyDictionary<string, bool> WorkspaceWrites { get; } = workspaceWrites;
            public IReadOnlyDictionary<string, string> WorkspaceModules { get; } = workspaceModules;
            public IReadOnlySet<string> ModulesWithReadOnlyWorkspaces { get; } = modulesWithReadOnlyWorkspaces;
        }
    }
}
