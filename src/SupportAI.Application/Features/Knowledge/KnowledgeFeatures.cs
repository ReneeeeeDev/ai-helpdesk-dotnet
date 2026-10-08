using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportAI.Application.Abstractions.Auth;
using SupportAI.Application.Abstractions.Data;
using SupportAI.Application.Abstractions.Messaging;
using SupportAI.Application.Abstractions.Tenancy;
using SupportAI.Application.Common.Exceptions;
using SupportAI.Contracts.Knowledge;
using SupportAI.Domain.Knowledge;
using SupportAI.Domain.Users;

namespace SupportAI.Application.Features.Knowledge;

public sealed record GetArticlesQuery(string? Search, string? Category, bool IncludeUnpublished) : IQuery<IReadOnlyList<KnowledgeArticleSummaryDto>>;

internal sealed class GetArticlesHandler(IApplicationDbContext db, ICurrentUser user, IKnowledgeSearch search)
    : IQueryHandler<GetArticlesQuery, IReadOnlyList<KnowledgeArticleSummaryDto>>
{
    public async Task<IReadOnlyList<KnowledgeArticleSummaryDto>> Handle(GetArticlesQuery query, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var hits = await search.SearchAsync(query.Search, query.Category, top: 20, ct);
            var ids = hits.Select(h => h.Id).ToList();
            var byId = await db.KnowledgeArticles.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
            return ids.Where(byId.ContainsKey).Select(id => byId[id].ToSummary()).ToList();
        }

        var includeUnpublished = query.IncludeUnpublished && user.IsStaff;
        var articles = db.KnowledgeArticles.AsNoTracking().Where(a => includeUnpublished || a.IsPublished);
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            var category = query.Category.Trim().ToLowerInvariant();
            articles = articles.Where(a => a.Category == category);
        }

        var list = await articles.OrderBy(a => a.Title).Take(200).ToListAsync(ct);
        return list.Select(a => a.ToSummary()).ToList();
    }
}

public sealed record GetArticleQuery(Guid Id) : IQuery<KnowledgeArticleDto>;

internal sealed class GetArticleHandler(IApplicationDbContext db, ICurrentUser user) : IQueryHandler<GetArticleQuery, KnowledgeArticleDto>
{
    public async Task<KnowledgeArticleDto> Handle(GetArticleQuery query, CancellationToken ct)
    {
        var article = await db.KnowledgeArticles.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == query.Id && (a.IsPublished || user.IsStaff), ct)
            ?? throw new NotFoundException("Artículo", query.Id);
        return article.ToDto();
    }
}

[AuthorizeRoles(UserRole.Agent, UserRole.Admin)]
public sealed record CreateArticleCommand(UpsertArticleRequest Article) : ICommand<KnowledgeArticleDto>;

[AuthorizeRoles(UserRole.Agent, UserRole.Admin)]
public sealed record UpdateArticleCommand(Guid Id, UpsertArticleRequest Article) : ICommand<KnowledgeArticleDto>;

[AuthorizeRoles(UserRole.Admin)]
public sealed record DeleteArticleCommand(Guid Id) : ICommand;

internal sealed class UpsertArticleRequestValidator : AbstractValidator<UpsertArticleRequest>
{
    public UpsertArticleRequestValidator()
    {
        RuleFor(a => a.Title).NotEmpty().MaximumLength(200);
        RuleFor(a => a.Body).NotEmpty().MaximumLength(KnowledgeArticle.MaxBodyLength);
        RuleFor(a => a.Category).MaximumLength(60);
        RuleForEach(a => a.Tags).NotEmpty().MaximumLength(40);
    }
}

internal sealed class CreateArticleValidator : AbstractValidator<CreateArticleCommand>
{
    public CreateArticleValidator() => RuleFor(c => c.Article).NotNull().SetValidator(new UpsertArticleRequestValidator());
}

internal sealed class UpdateArticleValidator : AbstractValidator<UpdateArticleCommand>
{
    public UpdateArticleValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Article).NotNull().SetValidator(new UpsertArticleRequestValidator());
    }
}

internal sealed class CreateArticleHandler(IApplicationDbContext db, ITenantContext tenant, ICurrentUser user)
    : ICommandHandler<CreateArticleCommand, KnowledgeArticleDto>
{
    public async Task<KnowledgeArticleDto> Handle(CreateArticleCommand command, CancellationToken ct)
    {
        var a = command.Article;
        var article = KnowledgeArticle.Create(tenant.RequiredTenantId, user.RequiredUserId, a.Title, a.Body, a.Category, a.Tags ?? [], a.IsPublished);
        db.KnowledgeArticles.Add(article);
        await db.SaveChangesAsync(ct);
        return article.ToDto();
    }
}

internal sealed class UpdateArticleHandler(IApplicationDbContext db) : ICommandHandler<UpdateArticleCommand, KnowledgeArticleDto>
{
    public async Task<KnowledgeArticleDto> Handle(UpdateArticleCommand command, CancellationToken ct)
    {
        var article = await db.KnowledgeArticles.FirstOrDefaultAsync(x => x.Id == command.Id, ct)
            ?? throw new NotFoundException("Artículo", command.Id);
        var a = command.Article;
        article.Update(a.Title, a.Body, a.Category, a.Tags ?? []);
        if (a.IsPublished)
        {
            article.Publish();
        }
        else
        {
            article.Unpublish();
        }

        await db.SaveChangesAsync(ct);
        return article.ToDto();
    }
}

internal sealed class DeleteArticleHandler(IApplicationDbContext db, TimeProvider clock) : ICommandHandler<DeleteArticleCommand>
{
    public async Task<Unit> Handle(DeleteArticleCommand command, CancellationToken ct)
    {
        var article = await db.KnowledgeArticles.FirstOrDefaultAsync(x => x.Id == command.Id, ct)
            ?? throw new NotFoundException("Artículo", command.Id);
        article.SoftDelete(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

internal static class KnowledgeMappings
{
    public static KnowledgeArticleSummaryDto ToSummary(this KnowledgeArticle a) =>
        new(a.Id, a.Title, a.Category, a.Tags, a.IsPublished, a.CreatedAt, a.UpdatedAt);

    public static KnowledgeArticleDto ToDto(this KnowledgeArticle a) =>
        new(a.Id, a.Title, a.Body, a.Category, a.Tags, a.IsPublished, a.AuthorId, a.CreatedAt, a.UpdatedAt);
}
