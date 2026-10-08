using SupportAI.Application.Common.Paging;
using SupportAI.Application.Features.Knowledge;
using C = SupportAI.Contracts.Common;
using DAi = SupportAI.Domain.Ai;
using DT = SupportAI.Domain.Tickets;
using DU = SupportAI.Domain.Users;

namespace SupportAI.Application.Tests.Common;

public class KeysetCursorTests
{
    [Theory]
    [InlineData(1042L, null)]
    [InlineData(7L, 3L)]
    [InlineData(1000L, 638_000_000_000_000_000L)]
    public void EncodeDecode_RoundTrips(long number, long? key)
    {
        var cursor = new KeysetCursor(number, key);

        KeysetCursor.TryDecode(cursor.Encode()).ShouldBe(cursor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("%%%")]
    [InlineData("bm90LWEtbnVtYmVy")] // "not-a-number"
    public void TryDecode_Garbage_ReturnsNull(string value) => KeysetCursor.TryDecode(value).ShouldBeNull();
}

public class KnowledgeSearchTextTests
{
    [Fact]
    public void Normalize_RemovesAccentsAndCase() =>
        KnowledgeSearch.Normalize("Facturación ÁGIL").ShouldBe("facturacion agil");

    [Fact]
    public void ExtractKeywords_DropsStopWordsShortWordsAndNumbers()
    {
        var keywords = KnowledgeSearch.ExtractKeywords("Hola, necesito exportar mis facturas del 2025 en CSV para el contador").ToList();

        keywords.ShouldContain("exportar");
        keywords.ShouldContain("facturas");
        keywords.ShouldContain("csv");
        keywords.ShouldNotContain("hola");
        keywords.ShouldNotContain("necesito");
        keywords.ShouldNotContain("2025");
        keywords.ShouldNotContain("el");
    }

    [Fact]
    public void ExtractKeywords_RanksRepeatedWordsFirst() =>
        KnowledgeSearch.ExtractKeywords("webhook firma webhook secreto webhook").First().ShouldBe("webhook");
}

/// <summary>Los enums de Contracts son un espejo de los de dominio: mismos nombres y valores.</summary>
public class EnumMappingTests
{
    public static TheoryData<Type, Type> Pairs => new()
    {
        { typeof(DT.TicketStatus), typeof(C.TicketStatus) },
        { typeof(DT.TicketPriority), typeof(C.TicketPriority) },
        { typeof(DT.TicketChannel), typeof(C.TicketChannel) },
        { typeof(DT.Sentiment), typeof(C.Sentiment) },
        { typeof(DT.MessageAuthorType), typeof(C.MessageAuthorType) },
        { typeof(DU.UserRole), typeof(C.UserRole) },
        { typeof(DAi.TriageResultStatus), typeof(C.TriageResultStatus) },
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void ContractEnum_MirrorsDomainEnum(Type domain, Type contract)
    {
        var domainMembers = Enum.GetValues(domain).Cast<object>().ToDictionary(v => v.ToString()!, Convert.ToInt32);
        var contractMembers = Enum.GetValues(contract).Cast<object>().ToDictionary(v => v.ToString()!, Convert.ToInt32);

        contractMembers.ShouldBe(domainMembers);
    }
}
