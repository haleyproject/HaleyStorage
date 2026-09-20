using Haley.Abstractions;
using Haley.Enums;
using Haley.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace Haley.Models {
    /// <summary>
    /// Extends <see cref="StorageReadFileRequest"/> with write-specific data:
    /// the original file name, write conflict mode, buffer size, and the upload stream.
    /// Used for single-shot uploads and as the request object for chunked upload initiation.
    /// </summary>
    public class StorageWriteRequest : StorageReadFileRequest, IVaultFileWriteRequest {
        public string OriginalName { get; set; } //actual file name.
        public ExistConflictResolveMode WriteConflictMode { get; set; } = ExistConflictResolveMode.ReturnError;
        public int BufferSize { get; set; } = 1024 * 80; //Default to 80KB
        public bool? ReplaceExistingFile { get; set; } //Applicable only when we are dealing with an existing file.. This is if CUID is present.
        public bool IsThumbnail { get; set; }
        internal bool IsDirectoryThumbnail { get; set; }
        public string Id { get; set; }
        public Stream FileStream { get; set; }

        /// <summary>Fluent override that returns <see cref="StorageWriteRequest"/> instead of the base type, enabling method chaining.</summary>
        public new StorageWriteRequest SetComponent(IVaultObject input, Enums.VaultObjectType type) { base.SetComponent(input, type);
            return this;
        }
        public StorageWriteRequest() : this(null, null, null) {

        }
        public StorageWriteRequest(string client_name) : this(client_name, null,null) {

        }
        public StorageWriteRequest(string client_name, string module_name) : this(client_name, module_name, null) {

        }
        public StorageWriteRequest(string client_name, string module_name,string workspace_name, bool isWsVirtual = false):base(client_name,module_name,workspace_name) {

        }

        /// <summary>Creates a shallow clone by mapping all properties onto a new <see cref="StorageWriteRequest"/>.</summary>
        public virtual object Clone() {
            var cloned = new StorageWriteRequest(this.Scope.Client?.DisplayName);
            //use map
            this.MapProperties(cloned);
            return cloned ;
        }

        /// <summary>Sets the original upload filename (used for extension detection and display name storage).</summary>
        public IVaultFileWriteRequest SetOriginalName(string name) {
            if (string.IsNullOrWhiteSpace(name)) return this;
            OriginalName = name;
            return this;
        }
    }
}
