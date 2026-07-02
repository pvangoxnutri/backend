using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Controllers;

[ApiController]
[Authorize]
public class PackingListController : ControllerBase
{
    private readonly AppDbContext _db;

    public PackingListController(AppDbContext db) => _db = db;

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<bool> IsMember(Guid tripId, Guid userId, CancellationToken ct) =>
        await _db.TripMembers.AnyAsync(tm => tm.TripId == tripId && tm.UserId == userId, ct);

    private async Task<bool> IsOwner(Guid tripId, Guid userId, CancellationToken ct) =>
        await _db.TripMembers.AnyAsync(tm => tm.TripId == tripId && tm.UserId == userId && tm.IsOwner, ct);

    // GET /api/trips/{tripId}/packing-list
    [HttpGet("api/trips/{tripId:guid}/packing-list")]
    public async Task<ActionResult> GetPackingList(Guid tripId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await IsMember(tripId, userId, ct)) return Forbid();

        var categories = await _db.PackingListCategories
            .Where(c => c.TripId == tripId)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt)
            .Select(c => new
            {
                id = c.Id,
                name = c.Name,
                sortOrder = c.SortOrder,
                createdByUserId = c.CreatedByUserId,
                items = c.Items
                    .OrderBy(i => i.SortOrder).ThenBy(i => i.CreatedAt)
                    .Select(i => new
                    {
                        id = i.Id,
                        text = i.Text,
                        isChecked = i.IsChecked,
                        sortOrder = i.SortOrder,
                        createdByUserId = i.CreatedByUserId,
                        checkedByUserId = i.CheckedByUserId,
                        checkedAt = i.CheckedAt,
                    }).ToList(),
            })
            .ToListAsync(ct);

        return Ok(categories);
    }

    // POST /api/trips/{tripId}/packing-list/categories
    [HttpPost("api/trips/{tripId:guid}/packing-list/categories")]
    public async Task<ActionResult> CreateCategory(Guid tripId, [FromBody] CreateCategoryDto dto, CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await IsMember(tripId, userId, ct)) return Forbid();
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest("Name is required.");

        var maxOrder = await _db.PackingListCategories
            .Where(c => c.TripId == tripId)
            .Select(c => (int?)c.SortOrder)
            .MaxAsync(ct) ?? -1;

        var category = new PackingListCategory
        {
            TripId = tripId,
            Name = dto.Name.Trim(),
            SortOrder = maxOrder + 1,
            CreatedByUserId = userId,
        };
        _db.PackingListCategories.Add(category);
        await _db.SaveChangesAsync(ct);

        return Created("", new { id = category.Id, name = category.Name, sortOrder = category.SortOrder, createdByUserId = category.CreatedByUserId, items = Array.Empty<object>() });
    }

    // PATCH /api/trips/{tripId}/packing-list/categories/{categoryId}
    [HttpPatch("api/trips/{tripId:guid}/packing-list/categories/{categoryId:guid}")]
    public async Task<ActionResult> UpdateCategory(Guid tripId, Guid categoryId, [FromBody] UpdateCategoryDto dto, CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await IsMember(tripId, userId, ct)) return Forbid();

        var category = await _db.PackingListCategories
            .FirstOrDefaultAsync(c => c.Id == categoryId && c.TripId == tripId, ct);
        if (category == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(dto.Name)) category.Name = dto.Name.Trim();
        category.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = category.Id, name = category.Name });
    }

    // DELETE /api/trips/{tripId}/packing-list/categories/{categoryId}
    [HttpDelete("api/trips/{tripId:guid}/packing-list/categories/{categoryId:guid}")]
    public async Task<ActionResult> DeleteCategory(Guid tripId, Guid categoryId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await IsMember(tripId, userId, ct)) return Forbid();

        var category = await _db.PackingListCategories
            .FirstOrDefaultAsync(c => c.Id == categoryId && c.TripId == tripId, ct);
        if (category == null) return NotFound();

        var isOwner = await IsOwner(tripId, userId, ct);
        if (!isOwner && category.CreatedByUserId != userId) return Forbid();

        _db.PackingListCategories.Remove(category);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // POST /api/trips/{tripId}/packing-list/categories/{categoryId}/items
    [HttpPost("api/trips/{tripId:guid}/packing-list/categories/{categoryId:guid}/items")]
    public async Task<ActionResult> CreateItem(Guid tripId, Guid categoryId, [FromBody] CreateItemDto dto, CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await IsMember(tripId, userId, ct)) return Forbid();
        if (string.IsNullOrWhiteSpace(dto.Text)) return BadRequest("Text is required.");

        var categoryExists = await _db.PackingListCategories
            .AnyAsync(c => c.Id == categoryId && c.TripId == tripId, ct);
        if (!categoryExists) return NotFound();

        var maxOrder = await _db.PackingListItems
            .Where(i => i.CategoryId == categoryId)
            .Select(i => (int?)i.SortOrder)
            .MaxAsync(ct) ?? -1;

        var item = new PackingListItem
        {
            CategoryId = categoryId,
            Text = dto.Text.Trim(),
            SortOrder = maxOrder + 1,
            CreatedByUserId = userId,
        };
        _db.PackingListItems.Add(item);
        await _db.SaveChangesAsync(ct);

        return Created("", new { id = item.Id, text = item.Text, isChecked = item.IsChecked, sortOrder = item.SortOrder, createdByUserId = item.CreatedByUserId, checkedByUserId = (Guid?)null, checkedAt = (DateTime?)null });
    }

    // PATCH /api/trips/{tripId}/packing-list/items/{itemId}
    [HttpPatch("api/trips/{tripId:guid}/packing-list/items/{itemId:guid}")]
    public async Task<ActionResult> UpdateItem(Guid tripId, Guid itemId, [FromBody] UpdateItemDto dto, CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await IsMember(tripId, userId, ct)) return Forbid();

        var item = await _db.PackingListItems
            .Include(i => i.Category)
            .FirstOrDefaultAsync(i => i.Id == itemId && i.Category.TripId == tripId, ct);
        if (item == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(dto.Text)) item.Text = dto.Text.Trim();
        if (dto.IsChecked.HasValue)
        {
            item.IsChecked = dto.IsChecked.Value;
            item.CheckedByUserId = dto.IsChecked.Value ? userId : null;
            item.CheckedAt = dto.IsChecked.Value ? DateTime.UtcNow : null;
        }
        item.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new { id = item.Id, text = item.Text, isChecked = item.IsChecked, checkedByUserId = item.CheckedByUserId, checkedAt = item.CheckedAt });
    }

    // DELETE /api/trips/{tripId}/packing-list/items/{itemId}
    [HttpDelete("api/trips/{tripId:guid}/packing-list/items/{itemId:guid}")]
    public async Task<ActionResult> DeleteItem(Guid tripId, Guid itemId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await IsMember(tripId, userId, ct)) return Forbid();

        var item = await _db.PackingListItems
            .Include(i => i.Category)
            .FirstOrDefaultAsync(i => i.Id == itemId && i.Category.TripId == tripId, ct);
        if (item == null) return NotFound();

        var isOwner = await IsOwner(tripId, userId, ct);
        if (!isOwner && item.CreatedByUserId != userId) return Forbid();

        _db.PackingListItems.Remove(item);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public record CreateCategoryDto(string Name);
public record UpdateCategoryDto(string? Name);
public record CreateItemDto(string Text);
public record UpdateItemDto(string? Text, bool? IsChecked);
