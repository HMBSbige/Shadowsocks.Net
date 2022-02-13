using Shadowsocks.Crypto.AEAD;
using Shadowsocks.Crypto.Stream;

namespace Shadowsocks.Crypto;

public static class ShadowsocksCrypto
{
	/// <summary>
	/// ss-subkey
	/// </summary>
	public static ReadOnlySpan<byte> InfoBytes => new byte[] { 0x73, 0x73, 0x2d, 0x73, 0x75, 0x62, 0x6b, 0x65, 0x79 };

	public const string NoneMethod = @"none";
	public const string PlainMethod = @"plain";
	public const string Rc4Method = @"rc4";
	public const string Rc4Md5Method = @"rc4-md5";
	public const string Rc4Md56Method = @"rc4-md5-6";
	public const string ChaCha20IetfMethod = @"chacha20-ietf";
	public const string ChaCha20Method = @"chacha20";
	public const string XChaCha20Method = @"xchacha20";
	public const string Salsa20Method = @"salsa20";
	public const string XSalsa20Method = @"xsalsa20";
	public const string Aes128CfbMethod = @"aes-128-cfb";
	public const string Aes192CfbMethod = @"aes-192-cfb";
	public const string Aes256CfbMethod = @"aes-256-cfb";
	public const string Aes128CtrMethod = @"aes-128-ctr";
	public const string Aes192CtrMethod = @"aes-192-ctr";
	public const string Aes256CtrMethod = @"aes-256-ctr";
	public const string Aes128GcmMethod = @"aes-128-gcm";
	public const string Aes192GcmMethod = @"aes-192-gcm";
	public const string Aes256GcmMethod = @"aes-256-gcm";
	public const string ChaCha20IetfPoly1305Method = @"chacha20-ietf-poly1305";
	public const string XChaCha20IetfPoly1305Method = @"xchacha20-ietf-poly1305";
	public const string Sm4CtrMethod = @"sm4-ctr";
	public const string Sm4GcmMethod = @"sm4-gcm";

	public static IShadowsocksCrypto Create(string method, string password)
	{
		return method switch
		{
			NoneMethod or PlainMethod => new NoneShadowsocksCrypto(password),
			Rc4Method => new Rc4ShadowsocksCrypto(password),
			Rc4Md5Method => new Rc4Md5ShadowsocksCrypto(password),
			Rc4Md56Method => new Rc4Md56ShadowsocksCrypto(password),
			Aes128CtrMethod => new Aes128CtrShadowsocksCrypto(password),
			Aes192CtrMethod => new Aes192CtrShadowsocksCrypto(password),
			Aes256CtrMethod => new Aes256CtrShadowsocksCrypto(password),
			Aes128CfbMethod => new Aes128CfbShadowsocksCrypto(password),
			Aes192CfbMethod => new Aes192CfbShadowsocksCrypto(password),
			Aes256CfbMethod => new Aes256CfbShadowsocksCrypto(password),
			ChaCha20IetfMethod => new ChaCha20IETFShadowsocksCrypto(password),
			ChaCha20Method => new ChaCha20ShadowsocksCrypto(password),
			Salsa20Method => new Salsa20ShadowsocksCrypto(password),
			XSalsa20Method => new XSalsa20ShadowsocksCrypto(password),
			XChaCha20Method => new XChaCha20ShadowsocksCrypto(password),
			Sm4CtrMethod => new Sm4CtrShadowsocksCrypto(password),
			Aes128GcmMethod => new Aes128GcmShadowsocksCrypto(password),
			Aes192GcmMethod => new Aes192GcmShadowsocksCrypto(password),
			Aes256GcmMethod => new Aes256GcmShadowsocksCrypto(password),
			ChaCha20IetfPoly1305Method => new ChaChaPoly1305ShadowsocksCrypto(password),
			XChaCha20IetfPoly1305Method => new XChaChaPoly1305ShadowsocksCrypto(password),
			Sm4GcmMethod => new Sm4GcmShadowsocksCrypto(password),
			_ => throw new ArgumentException($@"Invalid method: {method}", nameof(method))
		};
	}
}
