using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Intuit.Ipp.Exception;
using Newtonsoft.Json.Linq;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Providers.QuickBooks;
using SMS.Modules.Integration.Tests.QuickBooks.Support;
using static SMS.Modules.Integration.Tests.QuickBooks.Support.QboTestKit;

namespace SMS.Modules.Integration.Tests.QuickBooks;

/// <summary>
/// Every exception here is a genuine SDK exception, built the way SDK 14.7.1.6's FaultHandler builds it
/// (Fault JSON parsed by the SDK's own FaultHandler, then wrapped per HTTP status). The loopback tests
/// in <see cref="QuickBooksProviderSdkLoopbackTests"/> confirm the same shapes come out of real HTTP calls.
/// </summary>
public class QboErrorTranslatorTests
{
    private readonly QboErrorTranslator _translator = new();

    private ProviderResult<string> Translate(Exception ex, QboOperation op = QboOperation.Other) => _translator.Translate<string>(ex, op);

    // ── The SDK shapes this depends on ─────────────────────────────────────────────────────

    [Fact]
    public void Sdk_shape_400_validation_fault_is_wrapped_and_its_errors_copied_to_the_outer_exception()
    {
        var ex = Http400(ValidationFault("6240", "Duplicate Name Exists Error", "The name supplied already exists."));

        ex.GetType().Should().Be(typeof(IdsException));
        ex.ErrorCode.Should().Be("400");
        ex.InnerException.Should().BeOfType<ValidationException>();
        ex.InnerExceptions.Should().ContainSingle().Which.ErrorCode.Should().Be("6240");
    }

    [Fact]
    public void Sdk_shape_empty_response_is_a_communication_error()
    {
        var ex = NoResponse();

        ex.InnerException.Should().BeOfType<CommunicationException>();
        ex.ErrorCode.Should().BeNull();
    }

    // ── AuthRevoked ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Http_401_is_AuthRevoked_with_the_fault_code_and_tid()
    {
        var result = Translate(Http401());

        result.Outcome.Should().Be(ProviderOutcomeKind.AuthRevoked);
        result.ErrorCode.Should().Be("3200");
        result.IntuitTid.Should().Be("tid-401");
        result.Message.Should().Contain("AuthenticationFailed");
    }

    [Fact]
    public void Invalid_token_without_a_parsed_fault_is_AuthRevoked()
    {
        var result = Translate(new InvalidTokenException("Unauthorized-401"));

        result.Outcome.Should().Be(ProviderOutcomeKind.AuthRevoked);
        result.ErrorCode.Should().Be("InvalidTokenException");
    }

    [Theory]
    [InlineData("100")]
    [InlineData("3200")]
    [InlineData("003200")]
    [InlineData("3100")]
    [InlineData("6190")]
    public void Auth_and_company_status_codes_are_AuthRevoked_whatever_the_fault_type(string code)
    {
        var result = Translate(Http400(ValidationFault(code, "message", "detail")));

        result.Outcome.Should().Be(ProviderOutcomeKind.AuthRevoked);
        result.ErrorCode.Should().Be(code.TrimStart('0'));
    }

    [Fact]
    public void Authentication_fault_in_a_200_body_is_AuthRevoked()
    {
        var parsed = Parse(FaultJson("AuthenticationFault", ("999", "Token expired", null, null)))!;

        parsed.Should().BeOfType<SecurityException>();
        Translate(parsed).Outcome.Should().Be(ProviderOutcomeKind.AuthRevoked);
    }

    [Fact]
    public void Http_403_is_AuthRevoked()
    {
        var result = Translate(HttpEndpoint(403));

        result.Outcome.Should().Be(ProviderOutcomeKind.AuthRevoked);
        result.ErrorCode.Should().Be("403");
    }

    [Fact]
    public void An_unusable_connection_is_AuthRevoked_with_its_status()
    {
        var result = Translate(new ConnectionUnavailableException(ConnectionStatus.Expired, "Refresh token expired"));

        result.Outcome.Should().Be(ProviderOutcomeKind.AuthRevoked);
        result.ErrorCode.Should().Be("ConnectionUnavailable:Expired");
        result.Message.Should().Contain("Refresh token expired");
    }

    [Fact]
    public void A_revoked_grant_is_AuthRevoked()
    {
        Translate(new AuthorizationRevokedException("invalid_grant")).Outcome.Should().Be(ProviderOutcomeKind.AuthRevoked);
    }

    // ── Throttled ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Http_429_is_Throttled()
    {
        var result = Translate(Http429());

        result.Outcome.Should().Be(ProviderOutcomeKind.Throttled);
        result.ErrorCode.Should().Be("429");
        result.IntuitTid.Should().Be("tid-429");
    }

    [Fact]
    public void ThrottleExceeded_fault_code_is_Throttled()
    {
        var result = Translate(Http400(FaultJson("ServiceFault", ("003001", "message=ThrottleExceeded; errorCode=003001; statusCode=429", null, null))));

        result.Outcome.Should().Be(ProviderOutcomeKind.Throttled);
        result.ErrorCode.Should().Be("3001");
    }

    [Fact]
    public void A_bare_ThrottleExceededException_is_Throttled()
    {
        Translate(new ThrottleExceededException("slow down")).Outcome.Should().Be(ProviderOutcomeKind.Throttled);
    }

    // ── Duplicate / StaleObject / NotFound ─────────────────────────────────────────────────

    [Theory]
    [InlineData("6240", "Duplicate Name Exists Error")]
    [InlineData("6140", "Duplicate Document Number Error")]
    public void Duplicates(string code, string message)
    {
        var result = Translate(Http400(ValidationFault(code, message, "You must specify a different number.")), QboOperation.Create);

        result.Outcome.Should().Be(ProviderOutcomeKind.Duplicate);
        result.ErrorCode.Should().Be(code);
        result.IntuitTid.Should().Be("tid-400");
        result.Message.Should().StartWith(message);
    }

    [Fact]
    public void Stale_object_is_StaleObject()
    {
        var result = Translate(Http400(ValidationFault("5010", "Stale Object Error",
            "Stale Object Error : You and root were working on this at the same time.")), QboOperation.Update);

        result.Outcome.Should().Be(ProviderOutcomeKind.StaleObject);
        result.ErrorCode.Should().Be("5010");
        result.Message.Should().Be("Stale Object Error : You and root were working on this at the same time.",
            "a detail that already starts with the message is not repeated");
    }

    [Theory]
    [InlineData(nameof(QboOperation.Read))]
    [InlineData(nameof(QboOperation.Update))]
    [InlineData(nameof(QboOperation.Void))]
    [InlineData(nameof(QboOperation.Other))]
    public void Object_not_found_is_NotFound(string operation)
    {
        var result = Translate(Http400(ValidationFault("610", "Object Not Found",
            "Object Not Found : Something you're trying to use has been made inactive.")), Enum.Parse<QboOperation>(operation));

        result.Outcome.Should().Be(ProviderOutcomeKind.NotFound);
        result.ErrorCode.Should().Be("610");
    }

    [Fact]
    public void Object_not_found_on_a_create_is_a_refusal_of_a_reference()
    {
        var result = Translate(Http400(ValidationFault("610", "Object Not Found",
            "Object Not Found : Something you're trying to use has been made inactive.")), QboOperation.Create);

        result.Outcome.Should().Be(ProviderOutcomeKind.Refused);
    }

    [Fact]
    public void The_first_known_code_among_several_errors_decides()
    {
        var json = FaultJson("ValidationFault",
            ("2050", "String length is either shorter or longer than supported by specification", null, "Notes"),
            ("6240", "Duplicate Name Exists Error", null, null));

        Translate(Http400(json)).Outcome.Should().Be(ProviderOutcomeKind.Duplicate);
    }

    // ── Refused ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Validation_fault_with_an_element_is_Refused_on_that_field()
    {
        var result = Translate(Http400(ValidationFault("2050",
            "String length is either shorter or longer than supported by specification",
            "String length specified does not match the supported length. Min:0 wanted, Max:100 allowed.", "DisplayName")));

        result.Outcome.Should().Be(ProviderOutcomeKind.Refused);
        result.ErrorCode.Should().Be("2050");
        result.ErrorField.Should().Be("DisplayName");
        result.Message.Should().Contain("Max:100");
    }

    [Theory]
    [InlineData("Required parameter Line.Amount is missing in the request", "Line.Amount")]
    [InlineData("Required param CustomerRef is missing", "CustomerRef")]
    [InlineData("String length exceeded. Element name: PrimaryEmailAddr.Address", "PrimaryEmailAddr.Address")]
    [InlineData("Invalid value. Property: TxnDate", "TxnDate")]
    [InlineData("Property Name:Unrecognized field \"Foo\" (Class Customer), not marked as ignorable", "Foo")]
    public void Field_is_extracted_from_the_detail_when_there_is_no_element(string detail, string field)
    {
        var result = Translate(Http400(ValidationFault("2020", "Required param missing, need to supply the required value for the API", detail)));

        result.Outcome.Should().Be(ProviderOutcomeKind.Refused);
        result.ErrorField.Should().Be(field);
    }

    [Fact]
    public void No_field_when_nothing_names_one()
    {
        var result = Translate(Http400(ValidationFault("6000", "A business validation error has occurred while processing your request",
            "Business Validation Error: You must select a customer for this transaction.")));

        result.Outcome.Should().Be(ProviderOutcomeKind.Refused);
        result.ErrorCode.Should().Be("6000");
        result.ErrorField.Should().BeNull();
        result.Message.Should().Be("A business validation error has occurred while processing your request: " +
                                   "Business Validation Error: You must select a customer for this transaction.");
    }

    [Theory]
    [InlineData("2020")]
    [InlineData("2050")]
    [InlineData("2500")]
    [InlineData("6000")]
    [InlineData("4000")]
    public void Other_validation_codes_are_Refused(string code)
    {
        Translate(Http400(ValidationFault(code, "x", "y"))).Outcome.Should().Be(ProviderOutcomeKind.Refused);
    }

    [Fact]
    public void Validation_fault_in_a_200_body_is_Refused()
    {
        var parsed = Parse(ValidationFault("2500", "Invalid Reference Id", "Invalid Reference Id : Accounts element id 999 not found"))!;

        var result = Translate(parsed);

        result.Outcome.Should().Be(ProviderOutcomeKind.Refused);
        result.ErrorCode.Should().Be("2500");
    }

    [Fact]
    public void A_400_without_a_readable_fault_is_Refused()
    {
        var ex = new IdsException("BadRequest", "400", "System.Net.Requests", null);

        var result = Translate(ex);

        result.Outcome.Should().Be(ProviderOutcomeKind.Refused);
        result.ErrorCode.Should().Be("400");
    }

    // ── Transient (outcome unknown) ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(404)]
    public void Server_errors_and_404_are_Transient(int status)
    {
        var result = Translate(HttpEndpoint(status));

        result.Outcome.Should().Be(ProviderOutcomeKind.Transient);
        result.ErrorCode.Should().Be(status.ToString());
        result.IntuitTid.Should().Be("tid-5xx");
        result.Message.Should().Contain(status.ToString());
    }

    [Theory]
    [InlineData(502)]
    [InlineData(504)]
    public void Other_5xx_statuses_are_Transient(int status)
    {
        Translate(HttpOther(status)).Outcome.Should().Be(ProviderOutcomeKind.Transient);
    }

    [Fact]
    public void No_response_is_Transient()
    {
        var result = Translate(NoResponse());

        result.Outcome.Should().Be(ProviderOutcomeKind.Transient);
        result.ErrorCode.Should().Be("CommunicationException");
    }

    [Fact]
    public void Service_fault_is_Transient_not_Refused()
    {
        var result = Translate(Http400(FaultJson("ServiceFault", ("10000", "An application error has occurred while processing your request", "System Failure Error", null))));

        result.Outcome.Should().Be(ProviderOutcomeKind.Transient);
        result.ErrorCode.Should().Be("10000");
    }

    [Fact]
    public void System_fault_is_Transient()
    {
        var parsed = Parse(FaultJson("SystemFault", ("10000", "An application error has occurred", "System Failure Error: boom", null)))!;

        parsed.GetType().Should().Be(typeof(IdsException), "unknown fault types come back as a plain IdsException");
        Translate(parsed).Outcome.Should().Be(ProviderOutcomeKind.Transient);
    }

    public static TheoryData<Exception> NetworkFailures => new()
    {
        new TimeoutException("timed out"),
        new TaskCanceledException("HttpClient.Timeout"),
        new OperationCanceledException("cancelled elsewhere"),
        new WebException("The operation has timed out", WebExceptionStatus.Timeout),
        new HttpRequestException("Connection refused"),
        new IOException("Unable to read data from the transport connection"),
        new SocketException((int)SocketError.ConnectionReset),
        new Intuit.Ipp.Exception.SerializationException("Unexpected character"),
        new CommunicationException("x"),
        new EndpointNotFoundException("x"),
        new ServerTooBusyException("x"),
        new RetryExceededException("x"),
        new ServiceException("x"),
        new AggregateException(new TimeoutException("inner"))
    };

    [Theory]
    [MemberData(nameof(NetworkFailures))]
    public void Network_and_service_failures_are_Transient(Exception ex)
    {
        Translate(ex).Outcome.Should().Be(ProviderOutcomeKind.Transient);
    }

    public static TheoryData<Exception> Unexpected => new()
    {
        new InvalidOperationException("QuickBooks returned no Customer."),
        new NullReferenceException(),
        new IdsException("Something odd"),
        new FormatException("bad number"),
        new Exception("plain")
    };

    [Theory]
    [MemberData(nameof(Unexpected))]
    public void Anything_unexpected_is_Transient_never_Refused(Exception ex)
    {
        var result = Translate(ex);

        result.Outcome.Should().Be(ProviderOutcomeKind.Transient);
        result.ErrorCode.Should().NotBeNullOrEmpty();
        result.Message.Should().NotBeNullOrEmpty();
    }

    // ── Result details ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResponseJson_summarises_the_fault()
    {
        var result = Translate(Http400(ValidationFault("2050", "Too long", "Max:100", "DisplayName")));

        var json = JObject.Parse(result.ResponseJson!);
        json["outcome"]!.Value<string>().Should().Be("Refused");
        json["httpStatus"]!.Value<int>().Should().Be(400);
        json["intuitTid"]!.Value<string>().Should().Be("tid-400");
        json["errorCode"]!.Value<string>().Should().Be("2050");
        json["errors"]![0]!["errorCode"]!.Value<string>().Should().Be("2050", "the key must not be 'code', which the Redactor masks");
        json["errors"]![0]!["element"]!.Value<string>().Should().Be("DisplayName");
    }

    [Fact]
    public void Secrets_in_messages_are_redacted()
    {
        var result = Translate(new HttpRequestException("failed with Authorization: Bearer eyJabc.def.ghi"));

        result.Message.Should().NotContain("eyJabc.def.ghi");
        result.ResponseJson.Should().NotContain("eyJabc.def.ghi");
    }

    [Fact]
    public void Long_messages_and_codes_are_capped_to_the_log_columns()
    {
        var result = Translate(Http400(ValidationFault(new string('9', 150), new string('m', 5000), null)));

        result.ErrorCode!.Length.Should().BeLessThanOrEqualTo(QboErrorTranslator.MaxCodeLength);
        result.Message!.Length.Should().BeLessThanOrEqualTo(QboErrorTranslator.MaxMessageLength);
    }

    [Theory]
    [InlineData("003200", "3200")]
    [InlineData("0", "0")]
    [InlineData("000", "0")]
    [InlineData(" 6240 ", "6240")]
    [InlineData("QueryValidationError", "QueryValidationError")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Codes_are_normalised(string? raw, string? expected)
    {
        QboErrorTranslator.NormalizeCode(raw).Should().Be(expected);
    }
}
