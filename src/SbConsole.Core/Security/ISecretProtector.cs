namespace SbConsole.Core.Security;

public interface ISecretProtector
{
    byte[] Protect(string plaintext);
    string Unprotect(byte[] blob);
}
