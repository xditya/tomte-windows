using System.Data.OleDb;
using System.Runtime.InteropServices;

namespace Tomte.Core.Search;

/// <summary>
/// File search through the Windows Search index, the one Start uses, over its OLE DB provider. Covers only the folders
/// Windows indexes. Each search opens and closes its own connection, with OLE DB's session pooling off, so nothing of it
/// stays open or running between searches. Blocks for the query (a few hundred milliseconds): worker threads only.
/// </summary>
public static class WindowsIndex
{
    private const string Connection = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';OLE DB Services=-2;";

    /// <summary>
    /// The newest files and folders whose name has a word starting with each typed word. Empty for no words or a search
    /// the index refuses (only words it ignores, such as "the"); null when the index cannot be reached (the Windows Search
    /// service is off).
    /// </summary>
    public static IReadOnlyList<FileHit>? Search(IReadOnlyList<string> words, CancellationToken cancel)
    {
        string sql = FileQuery.IndexSql(words, FileQuery.Excluded());
        if (sql.Length == 0)
        {
            return [];
        }

        var hits = new List<FileHit>();
        try
        {
            using var connection = new OleDbConnection(Connection);
            connection.Open();
            using var command = new OleDbCommand(sql, connection);
            using var reader = command.ExecuteReader();
            while (!cancel.IsCancellationRequested && reader.Read())
            {
                if (reader[0] is string path && path.Length > 0)
                {
                    hits.Add(new(path, reader[1] is "Directory"));
                }
            }
        }
        catch (OleDbException e) when (e.ErrorCode is unchecked((int)0x80041605) or unchecked((int)0x80041607))
        {
            // QUERY_E_ALLNOISE: every word is one the index ignores. QUERY_E_TIMEDOUT: the index was too slow this time.
            return [];
        }
        catch (Exception e) when (e is OleDbException or InvalidOperationException or COMException)
        {
            return null;
        }

        return hits;
    }
}
