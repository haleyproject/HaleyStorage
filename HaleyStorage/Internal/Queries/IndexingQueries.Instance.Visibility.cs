namespace Haley.Internal;

internal partial class IndexingQueries {
    public partial class INSTANCE {
        public class VISIBILITY {
            public const string FOLDER = "(@allow_hidden = 1 or not exists (select 1 from dir_path hp join directory hd on hd.id = hp.ancestor where hp.descendant = dir.id and left(hd.name, 1) = '.'))";
            public const string FILE = "(@allow_hidden = 1 or not exists (select 1 from dir_path hp join directory hd on hd.id = hp.ancestor where hp.descendant = d.parent and left(hd.name, 1) = '.'))";
            public const string IS_HIDDEN = @"with recursive ancestors as (
                select id, parent, name from directory where id = @id
                union distinct
                select d.id, d.parent, d.name from directory d join ancestors a on d.id = a.parent
            ) select exists(select 1 from ancestors where left(name, 1) = '.');";
            public const string FILE_DIRECTORY = @"select d.parent from document d
                left join doc_version v on v.parent = d.id
                where (@version_id is not null and v.id = @version_id)
                   or (@version_cuid is not null and v.cuid = @version_cuid)
                   or (@document_cuid is not null and d.cuid = @document_cuid) limit 1;";
        }
    }
}
