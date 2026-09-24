using System.Text.RegularExpressions;
using System.Threading.Tasks;
namespace Legenda.App;
public interface IAuthenticationService
{
    Task<bool> SignInAsync(string login, string password);
}
public sealed class DatabaseAuthenticationService(DatabaseService database) : IAuthenticationService
{
    public async Task<bool> SignInAsync(string login, string password)
    {
        await Task.Delay(350);
        return database.Authenticate(login, password);
    }
}
/// <summary>Local demonstration only. Replace with an API client for production.</summary>
public sealed class DemoAuthenticationService : IAuthenticationService
{
    public static bool IsValidLogin(string login) => Regex.IsMatch(login, @"\A[a-zA-Z0-9._@-]{3,64}\z");
    public async Task<bool> SignInAsync(string login, string password)
    {
        await Task.Delay(350);
        return login == "admin" && password == "Legenda2026!";
    }
}
