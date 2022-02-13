using Shadowsocks.Crypto;
using Shadowsocks.SpeedTest;
using System.Diagnostics;

Console.WriteLine(@"Start Shadowsocks.SpeedTest..");
#if DEBUG
Console.WriteLine("On Debug mode");
#endif
if (Debugger.IsAttached)
{
	Console.WriteLine(@"Debugger attached!");
}

Console.WriteLine($@"OS Version: {Environment.OSVersion}");
Console.WriteLine($@".NET Version: {Environment.Version}");

CryptoTest.Test(ShadowsocksCrypto.NoneMethod);
CryptoTest.Test(ShadowsocksCrypto.Rc4Method);
CryptoTest.Test(ShadowsocksCrypto.Rc4Md5Method);
CryptoTest.Test(ShadowsocksCrypto.Rc4Md56Method);
CryptoTest.Test(ShadowsocksCrypto.ChaCha20IetfMethod);
CryptoTest.Test(ShadowsocksCrypto.ChaCha20Method);
CryptoTest.Test(ShadowsocksCrypto.XChaCha20Method);
CryptoTest.Test(ShadowsocksCrypto.Salsa20Method);
CryptoTest.Test(ShadowsocksCrypto.XSalsa20Method);
CryptoTest.Test(ShadowsocksCrypto.Aes128CfbMethod);
CryptoTest.Test(ShadowsocksCrypto.Aes192CfbMethod);
CryptoTest.Test(ShadowsocksCrypto.Aes256CfbMethod);
CryptoTest.Test(ShadowsocksCrypto.Aes128CtrMethod);
CryptoTest.Test(ShadowsocksCrypto.Aes192CtrMethod);
CryptoTest.Test(ShadowsocksCrypto.Aes256CtrMethod);
CryptoTest.Test(ShadowsocksCrypto.Aes128GcmMethod);
CryptoTest.Test(ShadowsocksCrypto.Aes192GcmMethod);
CryptoTest.Test(ShadowsocksCrypto.Aes256GcmMethod);
CryptoTest.Test(ShadowsocksCrypto.ChaCha20IetfPoly1305Method);
CryptoTest.Test(ShadowsocksCrypto.XChaCha20IetfPoly1305Method);
CryptoTest.Test(ShadowsocksCrypto.Sm4CtrMethod);
CryptoTest.Test(ShadowsocksCrypto.Sm4GcmMethod);
