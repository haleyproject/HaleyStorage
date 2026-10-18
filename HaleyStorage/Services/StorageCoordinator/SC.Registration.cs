using Haley.Abstractions;
using Haley.Enums;
using Haley.Models;
using Haley.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Xml;
using static Haley.Internal.IndexingQueries;

namespace Haley.Services {
    /// <summary>
    /// Partial class — vault hierarchy registration (client, module, workspace) and seed-config loading.
    /// Clients and modules are DB-only hierarchy nodes — they have no physical directory or path.
    /// Only workspaces have a physical directory: <c>BasePath / workspace-sharded-path</c>.
    /// </summary>
    public partial class StorageCoordinator : IStorageCoordinator {
        /// <summary>Convenience overload — registers a client by name with an optional password.</summary>
        public Task<IFeedback> RegisterClient(string client_name, string password = null, bool addDefaultModule = false) {
            return RegisterClient(new VaultObject(client_name), password, addDefaultModule);
        }
        /// <summary>Convenience overload — registers a module by name under the given client.</summary>
        public Task<IFeedback> RegisterModule(string module_name = null, string client_name = null) {
            return RegisterModule(new VaultObject(module_name), new VaultObject(client_name));
        }
        /// <summary>Convenience overload — registers a workspace by name under the given client and module.</summary>
        public Task<IFeedback> RegisterWorkSpace(string workspace_name = null, string client_name = null, string module_name = null, VaultNameMode content_control = VaultNameMode.Number, VaultNameParseMode content_pmode = VaultNameParseMode.Generate, bool? is_virtual = null, string providerKey = null, bool caseSensitive = false) {
            return RegisterWorkSpace(new VaultObject(workspace_name), new VaultObject(client_name), new VaultObject(module_name), content_control, content_pmode, is_virtual, providerKey, caseSensitive);
        }

        /// <summary>
        /// Registers a client in the indexer (DB-only — no physical directory is created).
        /// Clients are purely a hierarchy node; all physical storage is owned by workspaces.
        /// </summary>
        /// <param name="password">Plaintext password; defaults to <c>"admin"</c> when null.</param>
        public Task<IFeedback> RegisterClient(IVaultObject client, string password = null, bool addDefaultModule = false) {
            return RegisterClientInternal(client, password, addDefaultModule, createOnly: false);
        }

        async Task<IFeedback> RegisterClientInternal(IVaultObject client, string password, bool addDefaultModule, bool createOnly) {
            if (client == null) return new Feedback(false, "Name cannot be empty");
            if (!client.TryValidate(out var msg)) return new Feedback(false, msg);
            if (string.IsNullOrWhiteSpace(password)) password = DEFAULTPWD;

            var signing = RandomUtils.GetString(512);
            var encrypt = RandomUtils.GetString(512);
            var pwdHash = HashUtils.ComputeHash(password, HashMethod.Sha256);
            var clientInfo = client.MapProperties(new VaultClient(pwdHash, signing, encrypt, client.DisplayName));

            var result = new Feedback(true, $"Client {client.DisplayName} is registered");
            if (Indexer == null)
                return result.SetStatus(false).SetMessage("Storage registry indexer is not configured; client registration cannot be persisted.");
            var idxResult = await Indexer.RegisterClient(clientInfo, createOnly);
            if (idxResult?.Status != true)
                return result.SetStatus(false).SetMessage(idxResult?.Message ?? $"Unable to register client {client.DisplayName}.");
            result.Result = idxResult.Result;

            if (addDefaultModule) {
                var moduleResult = await RegisterModule(client_name: client.DisplayName);
                if (moduleResult?.Status != true)
                    return result.SetStatus(false).SetMessage(moduleResult?.Message ?? "Unable to register the default module.");
            }

            return result;
        }

        /// <summary>
        /// Registers a module under an existing client (DB-only — no physical directory is created).
        /// Also auto-registers a virtual default workspace under this module.
        /// </summary>
        public async Task<IFeedback> RegisterModule(IVaultObject module, IVaultObject client) {
            string msg = string.Empty;
            if (!module.TryValidate(out msg)) return new Feedback(false, msg);
            if (!client.TryValidate(out msg)) return new Feedback(false, msg);

            var moduleInfo = module.MapProperties(new VaultModule(client.Name, module.DisplayName));
            var result = new Feedback(true, $"Module {module.DisplayName} is registered");
            if (Indexer == null)
                return result.SetStatus(false).SetMessage("Storage registry indexer is not configured; module registration cannot be persisted.");
            var idxResult = await Indexer.RegisterModule(moduleInfo);
            if (idxResult?.Status != true)
                return result.SetStatus(false).SetMessage(idxResult?.Message ?? $"Unable to register module {module.DisplayName}.");
            result.Result = idxResult.Result;

            // Modules registered at runtime must inherit the persisted default provider profile.
            if (!await Indexer.HydrateModuleAsync(moduleInfo.Cuid.ToString("N")))
                return result.SetStatus(false).SetMessage($"Unable to load module adapter for '{client.Name}/{module.Name}'.");

            await EnsureModulesHaveDefaultProfileAsync();

            // A missing w always maps to the virtual default workspace. Ensure it exists without
            // updating an existing workspace's immutable naming or routing configuration.
            var workspaceResult = await EnsureDefaultWorkspaceAsync(client.DisplayName, module.DisplayName);
            if (workspaceResult?.Status != true)
                return result.SetStatus(false).SetMessage(workspaceResult?.Message ?? "Unable to register the default workspace.");
            return result;
        }

        /// <summary>
        /// Registers a workspace under an existing client+module.
        /// For physical workspaces (non-virtual), creates the directory at
        /// <c>BasePath / clientDir / moduleDir / _wsShardedPath</c>.
        /// Virtual workspaces are DB-only — no directory is created.
        /// </summary>
        /// <param name="content_control">Whether file identifiers are auto-increment numbers (<c>Number</c>) or compact-N GUIDs (<c>Guid</c>).</param>
        /// <param name="content_pmode">Whether file names are parsed from caller input or auto-generated by the indexer.</param>
        /// <param name="is_virtual">Explicit override. When null, derived from name: null/empty/default name → virtual.</param>
        /// <param name="providerKey">Provider to use for this workspace. Null = use the registered default.</param>
        /// <param name="caseSensitive">When true, client and module directory names preserve original casing; otherwise normalized via ToDBName().</param>
        public async Task<IFeedback> RegisterWorkSpace(IVaultObject wspace, IVaultObject client, IVaultObject module, VaultNameMode content_control = VaultNameMode.Number, VaultNameParseMode content_pmode = VaultNameParseMode.Generate, bool? is_virtual = null, string providerKey = null, bool caseSensitive = false) {
            string msg = string.Empty;
            bool isVirtual = is_virtual ?? (string.IsNullOrWhiteSpace(wspace.Name) || wspace.Name.Equals(VaultConstants.DEFAULT_NAME, StringComparison.OrdinalIgnoreCase));
            if (!isVirtual && !wspace.TryValidate(out msg)) throw new Exception(msg);
            if (!client.TryValidate(out msg)) throw new Exception(msg);
            if (!module.TryValidate(out msg)) throw new Exception(msg);
            module.UpdateCUID(client.Name, module.Name);

            var provider = (!string.IsNullOrWhiteSpace(providerKey) && _providers.TryGetValue(providerKey, out var p)) ? p : GetDefaultProvider();
            bool isFs = provider is FileSystemStorageProvider;
            string wsPath = string.Empty;

            // hasRealName: distinguishes a named workspace from the auto-created default (null name).
            // Client/module base dirs are tied to the provider, not to virtual status —
            // but only when there is a real workspace name. The default auto-workspace
            // created by RegisterModule (null name) must not trigger any directory creation.
            bool hasRealName = !string.IsNullOrWhiteSpace(wspace.Name) && !wspace.Name.Equals(VaultConstants.DEFAULT_NAME, StringComparison.OrdinalIgnoreCase);
            var clientDir = caseSensitive ? client.DisplayName : client.Name.ToDBName();
            var moduleDir = caseSensitive ? module.DisplayName : module.Name.ToDBName();
            var baseDir = Path.GetFullPath(Path.Combine(BasePath, clientDir, moduleDir));
            var canProvision = IsWriteAllowed(client.Name, module.Name, wspace.Name);
            if (isFs && canProvision) {
                // Always create client/module base dirs for any named workspace on FS.
                if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);
            }

            string wsSegment = string.Empty;
            if (!isVirtual && hasRealName) {
                var wsCarrier = new VaultStorable(wspace.DisplayName, VaultNameMode.Guid, VaultNameParseMode.Generate);
                wsSegment = GenerateBasePath(wsCarrier, VaultObjectType.WorkSpace).path;
                wsPath = wsSegment; // only the workspace segment is stored in DB
                if (isFs && canProvision) {
                    var wsFullPath = Path.GetFullPath(Path.Combine(baseDir, wsSegment));
                    if (!Directory.Exists(wsFullPath)) Directory.CreateDirectory(wsFullPath);
                }
            }

            var wsInfo = wspace.MapProperties(new VaultWorkSpace(client.Name, module.Name, wspace.DisplayName, isVirtual) { StorageRef = wsPath, Base = Path.Combine(clientDir, moduleDir), NameMode = content_control, ParseMode = content_pmode, CaseSensitive = caseSensitive });

            var result = new Feedback(true, $"Workspace {wspace.DisplayName} is registered");
            if (Indexer == null)
                return result.SetStatus(false).SetMessage("Storage registry indexer is not configured; workspace registration cannot be persisted.");
            if (canProvision && !isVirtual && hasRealName && isFs && !Directory.Exists(Path.GetFullPath(Path.Combine(baseDir, wsSegment))))
                result.SetStatus(false).SetMessage("Directory is not created. Please ensure if the WriteMode is turned ON or proper access is available.");

            if (!result.Status) return result;
            var idxResult = await Indexer.RegisterWorkspace(wsInfo);
            if (idxResult?.Status != true)
                return result.SetStatus(false).SetMessage(idxResult?.Message ?? $"Unable to register workspace {wspace.DisplayName}.");
            result.Result = idxResult.Result;
            return result;
        }

        async Task<bool> ActivateConfiguredModuleAsync(string client, string module) {
            client = client.ToDBName();
            module = module.ToDBName();
            var status = await ActivateModuleRuntimeAsync(client, module);
            if (!status.Hydrated || !status.AdapterLoaded) return false;

            _logger?.LogInformation(
                "Storage startup module ready: Client={Client}; Module={Module}; DefaultWorkspace=default; AdapterLoaded=true.",
                client, module);
            return true;
        }

        async Task<IFeedback> EnsureDefaultWorkspaceAsync(string client, string module) {
            var defaultWorkspaceCuid = StorageUtils.GenerateCuid(client, module, VaultConstants.DEFAULT_NAME);
            if (Indexer.TryGetComponentInfo<VaultWorkSpace>(defaultWorkspaceCuid, out _)
                || await Indexer.HydrateWorkspaceAsync(defaultWorkspaceCuid))
                return new Feedback(true, "Default workspace is available.");

            return await RegisterWorkSpace(
                VaultConstants.DEFAULT_NAME,
                client,
                module,
                VaultNameMode.Number,
                VaultNameParseMode.Generate,
                is_virtual: true);
        }

        /// <summary>
        /// Reads the <c>Seed:OSSSource</c> configuration section (or the provided <paramref name="section"/>)
        /// and activates each configured module, creating missing clients/modules when necessary.
        /// Ensures every named client exists, even when its optional password is omitted.
        /// Preserves existing client details and credentials; deduplicates registrations within a single call.
        /// </summary>
        /// <param name="section">Optional override section; reads from app config when null.</param>

        public async Task<IFeedback> RegisterFromSource(IConfigurationSection section = null) {
            try {
                var result = new Feedback();
                if (section == null) {
                    section = ResourceUtils.GenerateConfigurationRoot()?.GetSection($@"Seed:{VaultConstants.CONFIG_SOURCE}");
                    if (section == null) return result.SetMessage("Cannot proceed with empty configuration");
                }
                var sources = section.AsDictionaryList();
                var sourceList = sources.Where(p => p.Count > 0 && p.First().Value is Dictionary<string, object>).Select(q => ((Dictionary<string, object>)q.First().Value).Map<DSSRegInfo>()).ToList();
                _logger?.LogInformation("Storage startup sources: Count={SourceCount}.", sourceList.Count);
                ReplaceWritePolicies(sourceList);

                var clients = new List<string>();
                var modules = new List<string>();
                var wspaces = new List<string>();
                var failures = new List<string>();
                var skipped = 0;

                foreach (var source in sourceList) {
                    if (string.IsNullOrWhiteSpace(source.Client)) continue;
                    _logger?.LogInformation(
                        "Registering configured Storage source: Client={Client}; Module={Module}; Workspace={Workspace}.",
                        source.Client, source.Module, string.IsNullOrWhiteSpace(source.Workspace) ? VaultConstants.DEFAULT_NAME : source.Workspace);
                    var cliKey = source.Client.ToDBName();
                    var modKey = string.IsNullOrWhiteSpace(source.Module) ? null : $"{cliKey}_{source.Module.ToDBName()}";
                    try {
                        if (modKey is not null && !modules.Contains(modKey)) {
                            if (!new VaultObject(source.Client).TryValidate(out var validation)
                                || !new VaultObject(source.Module).TryValidate(out validation)) {
                                failures.Add(validation);
                                continue;
                            }
                            if (await ActivateConfiguredModuleAsync(source.Client, source.Module)) {
                                modules.Add(modKey);
                                if (!clients.Contains(cliKey)) clients.Add(cliKey);
                            }
                        }

                        if (!clients.Contains(cliKey)) {
                            var clientResult = await RegisterClientInternal(new VaultObject(source.Client), source.Password,
                                addDefaultModule: false, createOnly: true);
                            if (clientResult?.Status != true) {
                                failures.Add(clientResult?.Message ?? $"Unable to register client '{source.Client}'.");
                                continue;
                            }
                            clients.Add(cliKey);
                        }

                        if (modKey is null) continue;
                        if (!modules.Contains(modKey)) {
                            var moduleResult = await RegisterModule(source.Module, source.Client);
                            if (moduleResult?.Status != true) {
                                failures.Add(moduleResult?.Message ?? $"Unable to register module '{source.Module}'.");
                                continue;
                            }
                            if (!await ActivateConfiguredModuleAsync(source.Client, source.Module)) {
                                failures.Add($"Configured module '{source.Client}/{source.Module}' could not be activated.");
                                continue;
                            }
                            modules.Add(modKey);
                        }

                        if (string.IsNullOrWhiteSpace(source.Workspace)) {
                            var workspaceResult = await RegisterWorkSpace(null, source.Client, source.Module, source.Control, source.Parse, providerKey: source.ProviderKey, caseSensitive: source.CaseSensitive);
                            if (workspaceResult?.Status != true)
                                failures.Add(workspaceResult?.Message ?? $"Unable to register the default workspace for '{source.Client}/{source.Module}'.");
                            continue;
                        }
                        var wsKey = $"{modKey}_{source.Workspace.ToDBName()}";
                        if (!wspaces.Contains(wsKey)) {
                            var workspaceResult = await RegisterWorkSpace(source.Workspace, source.Client, source.Module, source.Control, source.Parse, source.IsVirtual, source.ProviderKey, source.CaseSensitive);
                            if (workspaceResult?.Status != true) {
                                failures.Add(workspaceResult?.Message ?? $"Unable to register workspace '{source.Workspace}'.");
                                continue;
                            }
                            wspaces.Add(wsKey);
                        }
                    } catch (StorageRegistrationTargetNotFoundException ex) {
                        // A missing parent must not prevent a later source from creating that identity.
                        if (ex.ObjectType == VaultObjectType.Client)
                            clients.Remove(cliKey);
                        else if (ex.ObjectType == VaultObjectType.Module)
                            modules.Remove($"{cliKey}_{source.Module.ToDBName()}");
                        skipped++;
                        _logger?.LogWarning(
                            "Skipping startup source for client {Client}, module {Module}, workspace {Workspace}: {Reason}",
                            source.Client, source.Module, source.Workspace ?? VaultConstants.DEFAULT_NAME, ex.Message);
                    }
                }

                await InitializePersistedRegistryState();

                _logger?.LogInformation(
                    "Storage startup registration completed: Clients={ClientCount}; Modules={ModuleCount}; Failures={FailureCount}; Skipped={SkippedCount}.",
                    clients.Count, modules.Count, failures.Count, skipped);

                if (failures.Count > 0) {
                    var message = string.Join(Environment.NewLine, failures.Distinct());
                    _logger?.LogError("Storage startup source registration failed: {Reason}", message);
                    return result.SetStatus(false).SetMessage(message);
                }
                return result.SetStatus(true).SetMessage(skipped == 0
                    ? "Successfully registered."
                    : $"Startup registration completed; skipped {skipped} source(s) whose client or module was not found.");
            } catch (Exception ex) {
                _logger?.LogError(ex, "Storage startup source registration failed.");
                return new Feedback(false, ex.Message);
            }
        }

        async Task InitializePersistedRegistryState() {
            // Restore persisted profile overrides without requiring configured seed identities.
            if (Indexer != null) {
                await Indexer.RehydrateModuleProfilesAsync();
                await Indexer.RehydrateWorkspaceProfilesAsync();
            }

            // Explicit profiles win; only modules still lacking a profile receive the default.
            await EnsureModulesHaveDefaultProfileAsync();
        }
    }
}
