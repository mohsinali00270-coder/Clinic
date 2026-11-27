using System.Text;
using System.Text.Json.Nodes;
using Clinic.Client.Core.Local;
using Clinic.Shared.Scheduling;
using SQLite;

namespace Clinic.Client.Core.Clinical;

/// <summary>
/// FR-PAT-03: patient search on the device. An FTS5 index over name, MRN, phone digits and the dynamic values
/// (CNIC etc.) gives prefix matching in any word order. Builds without FTS5 fall back to LIKE.
/// </summary>
public static class PatientSearchIndex
{
    private const string Table = "PatientSearch";

    public static async Task<bool> CreateAsync(SQLiteAsyncConnection connection)
    {
        try
        {
            await connection.ExecuteAsync(
                $"CREATE VIRTUAL TABLE IF NOT EXISTS {Table} USING fts5(PatientId UNINDEXED, Content, tokenize = 'unicode61 remove_diacritics 2')");
            return true;
        }
        catch (SQLiteException)
        {
            return false;
        }
    }

    public static bool Exists(SQLiteConnection c) =>
        c.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = ?", Table) > 0;

    /// <summary>Rebuilds the index rows of these patients (inside the caller's transaction).</summary>
    public static void Reindex(SQLiteConnection c, IEnumerable<Guid> patientIds)
    {
        if (!Exists(c)) return;
        foreach (var id in patientIds.Distinct())
        {
            c.Execute($"DELETE FROM {Table} WHERE PatientId = ?", id.ToString());
            var p = c.Find<LocalPatient>(id);
            if (p is null || p.IsDeleted) continue;
            c.Execute($"INSERT INTO {Table} (PatientId, Content) VALUES (?, ?)", id.ToString(), ContentOf(p));
        }
    }

    /// <summary>Adds rows for patients known to be new to the index (bulk loads: no delete or re-read per patient).</summary>
    public static void IndexNew(SQLiteConnection c, IEnumerable<LocalPatient> patients)
    {
        if (!Exists(c)) return;
        foreach (var p in patients)
            if (!p.IsDeleted) c.Execute($"INSERT INTO {Table} (PatientId, Content) VALUES (?, ?)", p.Id.ToString(), ContentOf(p));
    }

    public static string ContentOf(LocalPatient p)
    {
        var sb = new StringBuilder();
        sb.Append(p.FullName).Append(' ').Append(p.Mrn.Replace("-", " ")).Append(' ').Append(p.Mrn.Replace("-", ""));
        var phone = PatientRules.NormalizePhone(p.Phone);
        if (phone.Length > 0) sb.Append(' ').Append(phone).Append(' ').Append(phone.TrimStart('0'));
        if (!string.IsNullOrEmpty(p.DynamicData) && JsonNode.Parse(p.DynamicData) is JsonObject dyn)
        {
            foreach (var (_, v) in dyn)
            {
                if (v is JsonValue value && value.TryGetValue<string>(out var s) && s.Length is > 2 and < 100)
                    sb.Append(' ').Append(s).Append(' ').Append(s.Replace("-", ""));
            }
        }
        return sb.ToString();
    }

    /// <summary>Every word must match the start of some indexed word (e.g. "muh kh" finds "Muhammad Khan").</summary>
    public static async Task<List<Guid>> SearchAsync(LocalDatabase db, string query, int limit)
    {
        var words = query.Split([' ', ',', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0)
            .ToList();
        if (words.Count == 0) return [];

        if (db.SearchIndexAvailable)
        {
            var match = string.Join(" AND ", words.Select(w => $"\"{w}\"*"));
            var rows = await db.Connection.QueryScalarsAsync<string>(
                $"SELECT PatientId FROM {Table} WHERE {Table} MATCH ? ORDER BY rank LIMIT ?", match, limit);
            return rows.Select(Guid.Parse).ToList();
        }

        // Fallback without FTS5: LIKE on name / MRN / phone.
        var like = $"%{words[0]}%";
        var all = await db.Connection.QueryAsync<LocalPatient>(
            "SELECT * FROM Patients WHERE IsDeleted = 0 AND (FullName LIKE ? OR Mrn LIKE ? OR Phone LIKE ?) LIMIT ?", like, like, like, limit * 4);
        return all.Where(p => words.All(w => ContentOf(p).Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Take(limit).Select(p => p.Id).ToList();
    }
}
