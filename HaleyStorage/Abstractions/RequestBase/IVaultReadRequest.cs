using Haley.Enums;
using Haley.Models;

namespace Haley.Abstractions {
    public interface IVaultReadRequest {
        string CallID { get; } //Needed for tracking purpose
        bool GenerateCallId();
        long? Actor { get; set; }
        IVaultScope Scope { get; set; }
        string OverrideRef { get; set; }
        string RequestedName { get; set; } //This could be like "a32fbc213..." but override ref could be like "a3/2f/bc/..."
        bool ReadOnlyMode { get; set; }
        bool AllowHiddenDirectories { get; set; }
        IVaultReadRequest SetComponent(IVaultObject input, VaultObjectType type);
    }
}
