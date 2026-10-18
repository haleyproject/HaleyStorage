using Haley.Enums;

namespace Haley.Models {
    /// <summary>Identifies a registry parent that a successful registration lookup did not find.</summary>
    internal sealed class StorageRegistrationTargetNotFoundException : ArgumentException {
        public StorageRegistrationTargetNotFoundException(VaultObjectType objectType, string message) : base(message) {
            ObjectType = objectType;
        }

        public VaultObjectType ObjectType { get; }
    }
}
