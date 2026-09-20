using Haley.Abstractions;
using Haley.Enums;
using Haley.Utils;

namespace Haley.Models {
    /// <summary>
    /// Base read request that carries scope information (client, module, workspace, folder),
    /// an optional resolved <see cref="StorageReadRequest.OverrideRef"/>, and a per-call unique ID.
    /// Scope is exposed via <see cref="Scope"/> using a concrete <see cref="StorageScope"/> model.
    /// </summary>
    public class StorageReadRequest : IVaultReadRequest {
        bool callIdGenerated;

        public string CallID { get; protected set; } = Guid.NewGuid().ToString();
        public long? Actor { get; set; }
        public IVaultScope Scope { get; set; }
        public string OverrideRef { get; set; }
        public string RequestedName { get; set; }
        public bool ReadOnlyMode { get; set; }
        public bool AllowHiddenDirectories { get; set; }
        internal bool WorkspaceIsVirtual { get; private set; }

        /// <summary>
        /// Regenerates a fresh <see cref="StorageReadRequest.CallID"/> for this request.
        /// Can only be called once per request instance; subsequent calls are no-ops and return <c>false</c>.
        /// </summary>
        public bool GenerateCallId() {
            if (callIdGenerated) return false;
            CallID = Guid.NewGuid().ToString();
            callIdGenerated = true;
            return true;
        }

        /// <summary>
        /// Sets the client, module, or workspace component and recomputes all CUIDs to reflect
        /// the updated hierarchy.
        /// </summary>
        public virtual IVaultReadRequest SetComponent(IVaultObject input, Enums.VaultObjectType type) {
            switch (type) {
                case Enums.VaultObjectType.Client:
                    Scope.Client = input;
                    break;
                case Enums.VaultObjectType.Module:
                    Scope.Module = input;
                    break;
                case Enums.VaultObjectType.WorkSpace:
                    Scope.Workspace = input;
                    break;
            }
            UpdateCUID();
            return this;
        }

        void UpdateCUID() {
            if (Scope?.Client == null) return;
            if (Scope.Module != null) Scope.Module.UpdateCUID(Scope.Client.Name);
            if (Scope.Workspace != null) Scope.Workspace.UpdateCUID(Scope.Client.Name, Scope.Module?.Name);
        }

        /// <summary>Sets the workspace by name.</summary>
        public IVaultReadRequest SetWorkspace(string name, bool isVirtual = false) {
            Scope.Workspace = new VaultObject(name).UpdateCUID(Scope.Client?.Name, Scope.Module?.Name);
            WorkspaceIsVirtual = isVirtual || Scope.Workspace.Name.Equals(VaultConstants.DEFAULT_NAME, StringComparison.OrdinalIgnoreCase);
            return this;
        }

        public StorageReadRequest() : this(null, null, null) { }
        public StorageReadRequest(string client_name) : this(client_name, null, null) { }
        public StorageReadRequest(string client_name, string module_name) : this(client_name, module_name, null) { }

        public StorageReadRequest(string client_name, string module_name, string workspace_name) {
            Scope = new StorageScope();
            Scope.Client = new VaultObject(client_name).UpdateCUID();
            Scope.Module = new VaultObject(module_name).UpdateCUID(Scope.Client.Name);
            Scope.Workspace = new VaultObject(workspace_name).UpdateCUID(Scope.Client.Name, Scope.Module.Name);
            WorkspaceIsVirtual = Scope.Workspace.Name.Equals(VaultConstants.DEFAULT_NAME, StringComparison.OrdinalIgnoreCase);
        }
    }
}
