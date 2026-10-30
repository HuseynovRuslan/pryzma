using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Common;
using Meydan.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Tests;

/// <summary>
/// A plausible Azerbaijani mobile for every new account and every changed number (D12). Accounts opened before the
/// rule keep their numbers as they were typed, and can still save their profile.
/// </summary>
public sealed class PhoneFormatTests : IDisposable
{
    private readonly MeydanFactory _factory = new() { Settings = { ["Email:OutboxWorkerEnabled"] = "false" } };

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("501234567", "501234567")]
    [InlineData("050 123 45 67", "501234567")]
    [InlineData("050-123-45-67", "501234567")]
    [InlineData("(050) 123-45-67", "501234567")]
    [InlineData("+994 50 123 45 67", "501234567")]
    [InlineData("994501234567", "501234567")]
    [InlineData("+994 050 123 45 67", "501234567")]
    [InlineData("00994 50 123 45 67", "501234567")]
    [InlineData("+994 10 123 45 67", "101234567")]
    [InlineData("051 123 45 67", "511234567")]
    [InlineData("+994 55 123 45 67", "551234567")]
    [InlineData("060 123 45 67", "601234567")]
    [InlineData("+994 70 123 45 67", "701234567")]
    [InlineData("077 123 45 67", "771234567")]
    [InlineData("+994 99 123 45 67", "991234567")]
    public void Every_usual_way_of_writing_a_mobile_is_one_number(string typed, string national) =>
        Assert.Equal(national, PhoneNumbers.AzMobile(typed));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("+994 12 123 45 67")]        // a Baku landline
    [InlineData("018 123 45 67")]            // a regional landline
    [InlineData("+994 59 123 45 67")]        // no such mobile operator
    [InlineData("+994 50 123 45 6")]         // a digit short
    [InlineData("+994 50 123 45 678")]       // a digit long
    [InlineData("50123456")]                 // eight digits
    [InlineData("05012345678")]              // eleven
    [InlineData("+7 912 345 67 89")]         // a foreign mobile
    [InlineData("+90 532 123 45 67")]
    [InlineData("123456789012345")]          // fifteen digits
    [InlineData("1501234567")]               // ten digits without the trunk 0
    public void Anything_else_is_not_an_azerbaijani_mobile(string typed) =>
        Assert.Null(PhoneNumbers.AzMobile(typed));

    private static object Register(string phone) => new
    {
        firstName = "Aysel", lastName = "Məmmədova", phone,
        email = $"p-{Guid.NewGuid():N}@example.com",
        password = "secret123", confirmPassword = "secret123", acceptTerms = true,
    };

    [Fact]
    public async Task Registration_takes_a_mobile_however_it_is_written_and_refuses_anything_else()
    {
        var client = _factory.CreateClient();
        foreach (var phone in new[] { "+994 12 444 55 66", "+994 59 123 45 67", "05012345678", "+7 912 345 67 89" })
        {
            var res = await client.PostAsJsonAsync("/api/auth/register", Register(phone));
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            var body = await res.Content.ReadAsStringAsync();
            Assert.Contains("\"phone\"", body);
            Assert.Contains("Azərbaycan mobil nömrəsini yazın", body);
        }
        Assert.Equal(0, _factory.WithDb(db => db.Users.Count(u => u.FirstName == "Aysel")));

        // The usual shapes all register, and the number is kept as it was typed.
        foreach (var phone in new[] { "+994 050 711 22 33", "0517112234", "00994 55 711 22 35" })
            Assert.True((await client.PostAsJsonAsync("/api/auth/register", Register(phone))).IsSuccessStatusCode, phone);
        Assert.Equal(3, _factory.WithDb(db => db.Users.Count(u => u.FirstName == "Aysel")));
        Assert.Contains("+994 050 711 22 33", _factory.WithDb(db => db.Users.Select(u => u.PhoneNumber).ToList()));
    }

    [Fact]
    public async Task One_number_one_account_holds_across_shapes()
    {
        var client = _factory.CreateClient();
        Assert.True((await client.PostAsJsonAsync("/api/auth/register", Register("+994 70 300 40 50"))).IsSuccessStatusCode);
        var again = await client.PostAsJsonAsync("/api/auth/register", Register("+994 070 300 40 50"));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains(PhoneNumbers.AlreadyUsed, await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_account_opened_before_the_rule_keeps_its_number_and_can_still_save_its_profile()
    {
        var client = _factory.CreateClient();
        var token = (await (await client.PostAsJsonAsync("/api/auth/register", Register("+994 77 500 60 70")))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        client.UseToken(token);
        var id = (await (await client.GetAsync("/api/me/profile")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        // As an older account looks: a number in a shape the rule would refuse today.
        await _factory.WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == id)).PhoneNumber = "+994 12 444 55 66";
            await db.SaveChangesAsync();
        });

        // Saving the name with the number untouched works.
        Assert.True((await client.PutAsJsonAsync("/api/me/profile",
            new { firstName = "Aysel", lastName = "Əliyeva", phone = "+994 12 444 55 66" })).IsSuccessStatusCode);

        // Changing the number to another non-mobile does not.
        var bad = await client.PutAsJsonAsync("/api/me/profile", new { firstName = "Aysel", lastName = "Əliyeva", phone = "+994 12 444 55 77" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("Azərbaycan mobil nömrəsini yazın", await bad.Content.ReadAsStringAsync());

        // Changing it to a mobile does.
        Assert.True((await client.PutAsJsonAsync("/api/me/profile",
            new { firstName = "Aysel", lastName = "Əliyeva", phone = "050 444 55 77" })).IsSuccessStatusCode);
        Assert.Equal("050 444 55 77", _factory.WithDb(db => db.Users.Single(u => u.Id == id).PhoneNumber));
    }
}
