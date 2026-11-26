using System.Text;
using System.Text.RegularExpressions;
using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Domain.Forms;
using Clinic.Shared.Forms;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Forms;

/// <summary>§5.1 / FR-DYN-14: reusable, hierarchical option lists with CSV import. Lists and items sync to devices.</summary>
public sealed partial class LookupService(IAppDbContext db)
{
    [GeneratedRegex("^[a-z][a-z0-9_]{0,49}$")]
    private static partial Regex KeyRegex();

    public async Task<IReadOnlyList<LookupListDto>> ListAsync(CancellationToken ct) =>
        await db.LookupLists
            .OrderBy(l => l.Name)
            .Select(l => new LookupListDto(l.Id, l.Key, l.Name, l.ParentListKey, l.IsSystem,
                db.LookupItems.Count(i => i.ListId == l.Id)))
            .ToListAsync(ct);

    public async Task<LookupListDetailDto> GetAsync(Guid id, CancellationToken ct)
    {
        var list = await LoadAsync(id, ct);
        var items = await db.LookupItems.Where(i => i.ListId == id)
            .OrderBy(i => i.ParentCode).ThenBy(i => i.SortOrder).ThenBy(i => i.LabelEn)
            .Select(i => new LookupItemDto(i.Id, i.Code, i.LabelEn, i.LabelUr, i.ParentCode, i.SortOrder, i.IsActive))
            .ToListAsync(ct);
        return new LookupListDetailDto(list.Id, list.Key, list.Name, list.ParentListKey, list.IsSystem, items);
    }

    public async Task<LookupListDto> CreateAsync(SaveLookupListRequest request, CancellationToken ct)
    {
        var key = request.Key?.Trim() ?? "";
        if (!KeyRegex().IsMatch(key)) throw new RequestValidationException("key", "Key must be lower_snake_case (a–z, 0–9, _).");
        if (string.IsNullOrWhiteSpace(request.Name)) throw new RequestValidationException("name", "Name is required.");
        if (await db.LookupLists.AnyAsync(l => l.Key == key, ct)) throw new RequestValidationException("key", "A list with this key already exists.");
        await CheckParentAsync(request.ParentListKey, key, ct);

        var list = new LookupList { Id = Guid.CreateVersion7(), Key = key, Name = request.Name.Trim(), ParentListKey = Normalize.Optional(request.ParentListKey) };
        db.LookupLists.Add(list);
        await db.SaveChangesAsync(ct);
        return new LookupListDto(list.Id, list.Key, list.Name, list.ParentListKey, false, 0);
    }

    /// <summary>The key is immutable: forms refer to lists by key.</summary>
    public async Task<LookupListDto> UpdateAsync(Guid id, SaveLookupListRequest request, CancellationToken ct)
    {
        var list = await LoadAsync(id, ct);
        if (!string.Equals(request.Key?.Trim(), list.Key, StringComparison.Ordinal))
            throw new RequestValidationException("key", "A list's key cannot change because forms refer to it.");
        if (string.IsNullOrWhiteSpace(request.Name)) throw new RequestValidationException("name", "Name is required.");
        await CheckParentAsync(request.ParentListKey, list.Key, ct);

        list.Name = request.Name.Trim();
        list.ParentListKey = Normalize.Optional(request.ParentListKey);
        await db.SaveChangesAsync(ct);
        return new LookupListDto(list.Id, list.Key, list.Name, list.ParentListKey, list.IsSystem,
            await db.LookupItems.CountAsync(i => i.ListId == id, ct));
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var list = await LoadAsync(id, ct);
        if (list.IsSystem) throw new ConflictException("Built-in lists cannot be deleted. Deactivate items instead.");

        var marker = $"\"lookupList\":\"{list.Key}\"";
        var used = await db.Forms.AnyAsync(f => f.DraftJson.Contains(marker), ct)
                   || await db.FormVersions.AnyAsync(v => v.DefinitionJson.Contains(marker), ct);
        if (used) throw new ConflictException("A form uses this list. Remove it from the form first.");
        if (await db.LookupLists.AnyAsync(l => l.ParentListKey == list.Key, ct))
            throw new ConflictException("Another list uses this one as its parent.");

        list.IsDeleted = true;
        foreach (var item in await db.LookupItems.Where(i => i.ListId == id).ToListAsync(ct)) item.IsDeleted = true;
        await db.SaveChangesAsync(ct);
    }

    public async Task<LookupItemDto> SaveItemAsync(Guid listId, Guid? itemId, SaveLookupItemRequest request, CancellationToken ct)
    {
        var list = await LoadAsync(listId, ct);
        var code = request.Code?.Trim() ?? "";
        if (code.Length is 0 or > 50) throw new RequestValidationException("code", "Code is required (max 50 characters).");
        if (string.IsNullOrWhiteSpace(request.LabelEn)) throw new RequestValidationException("labelEn", "English label is required.");
        var parent = Normalize.Optional(request.ParentCode);
        await CheckParentCodeAsync(list, parent, ct);

        LookupItem item;
        if (itemId is { } id)
        {
            item = await db.LookupItems.FirstOrDefaultAsync(i => i.Id == id && i.ListId == listId, ct) ?? throw new NotFoundException("Lookup item", id);
            if (item.Code != code)
                throw new RequestValidationException("code", "An item's code cannot change because records store it. Add a new item and deactivate this one.");
        }
        else
        {
            if (await db.LookupItems.AnyAsync(i => i.ListId == listId && i.Code == code, ct))
                throw new RequestValidationException("code", "This code is already in the list.");
            item = new LookupItem { Id = Guid.CreateVersion7(), ListId = listId, ListKey = list.Key, Code = code };
            db.LookupItems.Add(item);
        }

        item.LabelEn = request.LabelEn.Trim();
        item.LabelUr = Normalize.Optional(request.LabelUr);
        item.ParentCode = parent;
        item.SortOrder = request.SortOrder;
        item.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return new LookupItemDto(item.Id, item.Code, item.LabelEn, item.LabelUr, item.ParentCode, item.SortOrder, item.IsActive);
    }

    /// <summary>
    /// FR-DYN-14 bulk import. Header row required; columns: code, labelEn, labelUr, parentCode, sortOrder.
    /// Existing codes are updated, new ones added; bad lines are reported and skipped.
    /// (Excel: save the sheet as CSV UTF-8.)
    /// </summary>
    public async Task<ImportLookupItemsResult> ImportCsvAsync(Guid listId, ImportLookupItemsRequest request, CancellationToken ct)
    {
        var list = await LoadAsync(listId, ct);
        var rows = ParseCsv(request.Csv ?? "");
        if (rows.Count == 0) throw new RequestValidationException("csv", "The file is empty.");

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string name) => header.IndexOf(name.ToLowerInvariant());
        var (cCode, cEn, cUr, cParent, cSort) = (Col("code"), Col("labelEn"), Col("labelUr"), Col("parentCode"), Col("sortOrder"));
        if (cCode < 0 || cEn < 0) throw new RequestValidationException("csv", "The first row must be a header with at least 'code' and 'labelEn' columns.");

        var existing = await db.LookupItems.Where(i => i.ListId == listId).ToDictionaryAsync(i => i.Code, ct);
        var parentCodes = list.ParentListKey is null ? null
            : (await db.LookupItems.Where(i => i.ListKey == list.ParentListKey).Select(i => i.Code).ToListAsync(ct)).ToHashSet();

        int added = 0, updated = 0;
        var errors = new List<string>();
        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            string? Cell(int c) => c >= 0 && c < row.Count && !string.IsNullOrWhiteSpace(row[c]) ? row[c].Trim() : null;

            var code = Cell(cCode);
            var en = Cell(cEn);
            if (code is null && en is null) continue;
            if (code is null || code.Length > 50) { errors.Add($"Line {r + 1}: code is missing or longer than 50 characters."); continue; }
            if (en is null) { errors.Add($"Line {r + 1}: labelEn is missing."); continue; }

            var parent = Cell(cParent);
            if (parentCodes is not null && parent is not null && !parentCodes.Contains(parent))
            {
                errors.Add($"Line {r + 1}: parent code '{parent}' is not in '{list.ParentListKey}'.");
                continue;
            }
            var sort = int.TryParse(Cell(cSort), out var s) ? s : 0;

            if (!existing.TryGetValue(code, out var item))
            {
                item = new LookupItem { Id = Guid.CreateVersion7(), ListId = listId, ListKey = list.Key, Code = code };
                db.LookupItems.Add(item);
                existing[code] = item;
                added++;
            }
            else
            {
                updated++;
            }
            item.LabelEn = en;
            item.LabelUr = Cell(cUr) ?? item.LabelUr;
            item.ParentCode = parent ?? item.ParentCode;
            item.SortOrder = sort;
            item.IsActive = true;
        }

        await db.SaveChangesAsync(ct);
        return new ImportLookupItemsResult(added, updated, errors);
    }

    /// <summary>Creates any missing built-in lists and items for the current tenant (idempotent).</summary>
    public static async Task EnsureDefaultsAsync(IAppDbContext db, CancellationToken ct = default)
    {
        foreach (var def in DefaultLookups.All)
        {
            var list = await db.LookupLists.FirstOrDefaultAsync(l => l.Key == def.Key, ct);
            if (list is null)
            {
                list = new LookupList { Id = Guid.CreateVersion7(), Key = def.Key, Name = def.Name, ParentListKey = def.ParentListKey, IsSystem = true };
                db.LookupLists.Add(list);
            }

            var codes = (await db.LookupItems.Where(i => i.ListKey == def.Key).Select(i => i.Code).ToListAsync(ct)).ToHashSet();
            var order = 0;
            foreach (var item in def.Items)
            {
                order += 10;
                if (codes.Contains(item.Code)) continue;
                db.LookupItems.Add(new LookupItem
                {
                    Id = Guid.CreateVersion7(), ListId = list.Id, ListKey = def.Key, Code = item.Code,
                    LabelEn = item.En, LabelUr = item.Ur, ParentCode = item.Parent, SortOrder = order,
                });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------------

    private async Task CheckParentAsync(string? parentKey, string ownKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(parentKey)) return;
        if (parentKey == ownKey) throw new RequestValidationException("parentListKey", "A list cannot be its own parent.");
        if (!await db.LookupLists.AnyAsync(l => l.Key == parentKey, ct))
            throw new RequestValidationException("parentListKey", $"Parent list '{parentKey}' does not exist.");
    }

    private async Task CheckParentCodeAsync(LookupList list, string? parentCode, CancellationToken ct)
    {
        if (parentCode is null) return;
        if (list.ParentListKey is null) throw new RequestValidationException("parentCode", "This list has no parent list.");
        if (!await db.LookupItems.AnyAsync(i => i.ListKey == list.ParentListKey && i.Code == parentCode, ct))
            throw new RequestValidationException("parentCode", $"'{parentCode}' is not in '{list.ParentListKey}'.");
    }

    private async Task<LookupList> LoadAsync(Guid id, CancellationToken ct) =>
        await db.LookupLists.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("Lookup list", id);

    /// <summary>RFC 4180-style CSV: commas, double-quoted fields, "" escapes, CRLF or LF.</summary>
    public static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
                continue;
            }

            switch (c)
            {
                case '"': quoted = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); break;
                case '\r': break;
                case '\n': row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = []; break;
                case '﻿' when i == 0: break;
                default: cell.Append(c); break;
            }
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}
