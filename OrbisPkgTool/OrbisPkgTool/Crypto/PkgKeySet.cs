using System.Numerics;
using System.Security.Cryptography;

namespace OrbisPkgTool.Crypto;

/// <summary>
/// RSA key material used by the PS4 PKG format.
///
/// These are the well-known public/private keys of the PS4 PKG scheme
/// (leaked Sony SDK keys, used by every PS4 package tool). The patched
/// "Custom PKG Key" builds of orbis-pub-cmd embed a different key — when
/// that key is extracted from the binary, provide it via
/// <see cref="PkgKeySet.WithCustomKeys"/>.
/// </summary>
public sealed class PkgKeySet
{
    /// <summary>
    /// Private key for RSA key index 3 — used to decrypt ENTRY_KEYS[3]
    /// (the "derived key 3" / dk3) and the image key chain.
    /// </summary>
    public RSAParameters DerivedKey3 { get; init; }

    /// <summary>
    /// Full RSA keypair used for fake PKGs ("FakeKeyset") — used to
    /// RSA-decrypt the image key into the PFS EKPFS.
    /// </summary>
    public RSAParameters FakeKeyset { get; init; }

    /// <summary>The standard key set (leaked Sony keys).</summary>
    public static PkgKeySet Standard { get; } = CreateStandard();

    /// <summary>
    /// The 7 RSA public key moduli used by the ENTRY_KEYS table
    /// (key[i] = RSA-encrypt(dk_i, PkgPublicKeys[i])); key 3 also signs the
    /// PKG header digest. Transcribed from the public scene constants.
    /// </summary>
    public static readonly byte[][] PkgPublicKeys =
    {
        Hex("d6aa0c5c0d6dc9e5ee28f9aa8dbc7236699617b65f8a4d969c8841330cb3fba0d6332d202fe30dade07a3e4a282f76291b4f8a347436c8c940464db2beedcb723e81ffa3ac52a24513c781b2215cbe979d070e8fc846e9bba6a56ed44887bbe6f60c486650109aab0f36b01f28e056e408d376629f0703263676b58eb37392665857943acfbc212ba008ba2c378118f7d81ea91a456895bf16861735309a79bfa9db96c21f0d9ffc8b3b4f81eaad87f6730f709a2918398e9c369a23bc94069b1b7b34a18b5f06c47965e965b3fbf6132fb7c0b54e488eb661a995d83e7f57e07b4e2125096f5f474d333c0634ae85e89d4806bdd1ee0c9cef63414c26fdc88f"),
        Hex("b96953eea54b1eb2f715eab62172bcec4d8fd39d483609d4f632043aab2057d9510fed357af9863cc8cf652427dc86b2816c480d5f30cb16c733b43ed38cc19ce21ad682c1ed9226a2408bd4c0807f9fbda64b942092f7f42dcb88e5135f92d2b1b9097995ab0e42e3ca3691159deda93ac1b0a218d71e128ad2db114fbde5328b613753ea4a4ae6fdf94b4d8ede120ebf414e3c76e0c9398cd92db8a5f48833f3364fa98bbb9289ecb2a8de9fd9cfeda74e55769bed39c5e1ab0c5d89075e2b339c6da748aa2c5799a7d48aff910a554aaf344a07cced8c1de45c54d6b4bdda5995f16e64c6da016556254779855e4c9768e7fddfd7d7df56bd173fa5ddcedd"),
        Hex("9d2a60a2538fa218d1a636de7cce7af8950b726b52753a2419a4545ca75eccb343abae8384ca0b7ffd269d7e4d369d0547aada16f33a56eb9e37657cfcd93edee62bab0978f15d99dd57ba5b17a65055d5939230d77a3bd47c5dbd1e78d6d2c7c184d3bd4e7fb8927db6e6ec61d81796bd31fbdbb6ac58df6943c8a8cacf0308f46f28b8ff975a7d7331dbb362d40736403c5d6597a54fd1dad91534be98c80a33741413814c51d0eee4fa5660c42f7ca6a9dd0a1110bdf498b1f1c9f81e7431bdd6ce91d0abdb1ec93f0340947a6e6bf04c78df463163c3b3208da239354e6ad9a78d9fce9da46042137791524d45bfc84696b87c0a7b2cfadf3943ce86dde9"),
        Hex("d212fc335f6ddb831609628b0356273782d477853529392d526b8c4c8cfb06c1845be7d4f7bcd24e6245cd2abbd77776453655273fb3f5f98eda4befaa59aeb39bea5498d206326a58312ae0d44f90b50a7decf43a9c52672d99318e0c43e682fe0746e12e50d41f2d2f7ed908ba06b3bf2e203f4e3ffe44ffaa50435791699449158282e40f4c8d9d2cc95b1d64bf888bd4c594e76547841ee57910fb989347b97d8512a640982cf792bc951932ede890560d65c1aa78c62e54fd5f54a1f67ee5e05f61c120b4b9b4330870e4df8956ed012946775f8cb8a9f51e2eb3b9bfe009b78d28d4a6c3b81e1f07ebb4120b95b88530fddc3913d07cdc8fedf9c9a3c1"),
        Hex("c9b2402b79556af9605e098644e865579f3df6f4800d230cd5ee4bc18d5a782a94224fd9959d863d48b3b97c6fb8939fffccc39019a464959e6bda17240be0acf6b2bbbe1ccca2e377fb410cdce3389e86a26fe5a67d9694beffc76fce4c829b679108b2c3b472f4ad2f4c4ca3f4c9712e3acc1e50d19941c55097b41004e8ab4e2b897ca8e1bfea7fac3c15e5b2394c449d823674bf0a84232bcabf01193f5206fb5aac28c68fbf70057033b4e6cc854e54cd181c380058056192c945a972f0690089bcb2342b79f68f536e8050c38388e736838c7ddf67433f5678527c09abac80c58498716116d78c05e003e1ccbaa7a7416d35487ca06d5b9ec2d24395c7"),
        Hex("e356f16b5dff3c7dff42e79a32c144e83bde58df19bb9c4a0f5aff153902de7665bf2a7162ee0d34a28d73718393ea0591231743c806381a95e5806ad0acdd8c91fb1fd7a21a693d037cdd8e2db0b24cc4ab73ce65a1a3e6536341920b6e0877772b9762c801df3c4b9a392cd2abde7d27e7a76a997da39e6e5a2baf172374f0e6f7d07e1af7394bb79dba813af6757e020a1b86f70465b7571d50c982348f54610c0a4bb8479ffcacc40f18bc3e80bddfa92dc88c89a7df5566c2b0ee5a238600eaa4851930ece86c0b302e3e6baac8d0185ac049002b3abc754cd3586a76424fc45ab1d1d72711fd5b4cb793708299de23f2c3de096c2ba7f264bf95489cd1"),
        Hex("e9cac344b4d96a2c5c255df0f1848c800d55f87e26326c3b0c1a14462d5fd057d4a0b87c8bb6880ba9d3ac282c38c06d0a30fe80d71ab481d6a17622198db04cc30f23382b1ba12b1bcb7ee0c5fc79960a537a11b3230471e63f2caeedd549b3255fda807af1af91db1770a5221c5cafe606cde2118f6842e7942d8f25d23cc4af98679fcf16be701e79b8c81f7046493906a1ff9e011ec0cf1dc8048061c22c7869b23b4da20fdbb764b465baae2fc69b09fb6e497ce1a0182d6662abdec3c960fa67f3505526ae0fb5e0cdd3d9b673e299a6e1badd478d546512ebe924ff26c2b8af07921a3b123171130363d0a6314d559599214cec48fd09e07585465b85"),
};

    /// <summary>A key set with custom (CyB1K "Custom PKG Key") material.</summary>
    public static PkgKeySet WithCustomKeys(RSAParameters derivedKey3, RSAParameters fakeKeyset) =>
        new() { DerivedKey3 = derivedKey3, FakeKeyset = fakeKeyset };

    private static PkgKeySet CreateStandard() => new()
    {
        DerivedKey3 = new RSAParameters
        {
            Modulus = Hex("d212fc335f6ddb831609628b0356273782d477853529392d526b8c4c8cfb06c1845be7d4f7bcd24e6245cd2abbd77776453655273fb3f5f98eda4befaa59aeb39bea5498d206326a58312ae0d44f90b50a7decf43a9c52672d99318e0c43e682fe0746e12e50d41f2d2f7ed908ba06b3bf2e203f4e3ffe44ffaa50435791699449158282e40f4c8d9d2cc95b1d64bf888bd4c594e76547841ee57910fb989347b97d8512a640982cf792bc951932ede890560d65c1aa78c62e54fd5f54a1f67ee5e05f61c120b4b9b4330870e4df8956ed012946775f8cb8a9f51e2eb3b9bfe009b78d28d4a6c3b81e1f07ebb4120b95b88530fddc3913d07cdc8fedf9c9a3c1"),
            D = Hex("32d903908fbdb08f572b285e0b8db3ea5cd17ea890888cdd6a80bbb1dfc1f70daa32f0b77ccb88800e8b64b0be4cd60e9b8c1e2a64e1f35cd77601415e935c94fedd4662c31b5ae2a0bc2debc3980aa7b7856970682b644ab31fcc7ddc7c26f477f65cf2ae5a442dd3ab16620419bafb90ffe23050896ecb56b2ebc09116925e308eaec7945dfd35e120f8ad3ebc08bfc036749fd5bb5208fd0666f37ab304f475295de95faa1030b20f5a1ac12ab3fecb21ad80ec8f20091cdbc55894c29cc6ce82653e5790bca98b06b4f072f677df9864f1ecfe372dbcae8c08811fc3c9891ac742824b2edc8e8d73ceb1cc01d90870873c4408ec498f815ae240ff77fc0d"),
            P = Hex("f967ad9912310c56a22e161c46b34d5b43be42a2f686968042c3c73fc342f58749339f075d6e2c04fde3e1b2ae0a0cf0c7a61ca16350c8099c5124526c5e5ebd1e2706bbbc9e94e135d46db3cb3c68dd68b3fe6ccb8d8220762363b7e96810014edcba275d01c12d805e2baf826bd884b6105286a7898eae9ae289c6f7d587fb"),
            Q = Hex("d7a10f9a8bf2c91195329a8cf0d94047f568a00dbdc1fc432f65f9c3610f257754add758ac8440608d3ff3658975b5c62c511a2f1f22e4431154bec9b4c7b51b050bbc569acd4ad973685e5cfb92b78b0dfff507cab4c89b963c079e3e6b2a11f28ab18ad72e1ba5532406ed50b89067b1e241c69201ee10f061bbfbb27d4a73"),
            DP = Hex("52cc2da09c9e75e728ee3ddee345d14f941cccc88729453b8d6eab6e2aa7c71543a3048f905febf3384a77fa36b71576b6011a8e258782f155d8c6432ac0e598c932d1946fd901ba0681e06d88f2242a2501645cbff2d999673ef672eee4e2335cf80040e32a9af43d2286443cfb0aa57c3fccf5f116c4ac88b4de6294926a13"),
            DQ = Hex("7c9dad39e0d560149448197f8895d58b80ad858a4b773785d077bbbf89714a72cb726838ec02c67dc6440633511cc0ff958f0d75dc25bb0b7391a96d42d803b768d41e7562a37035797800c8f5ef15b9fc4e475ac870705b5298c0c2584a7096ccb810e12f788b2ba17ff9acdef0bb2be266e3229231215792c4b8f23e762037"),
            InverseQ = Hex("459755d422085ef35cb4057afdaa4242ad9a8ca06cbb1d6854546e3e32e3537376f13e01ead3cfebeb233ec0beceec2c895fa8273a4cb7e674bc454c26c825ff34632537e14810c193a6afebbaa3a2f13def63d8f4fdd3eee25de933ccadba755c85afcea93dd1a217f3f698b3508e5ef6eb028ea162a7d62cec91ff1540d2e3"),
            Exponent = Hex("010001"),
        },
        FakeKeyset = new RSAParameters
        {
            Modulus = Hex("c6cf71e7e59af0d12a2c458bf92a0ec143058bc37117801dcd497dde359d259ba0d7a0f27d6c087eaa5502682b23c644b84418eb56cf16a24803c9e74f87eb3d30c31588bf20e79dff770cde1d241e63a94f8abf5bbe601968333bfced9f474e5ff8eacb3d00bd6701f92c6dc6ac1364e76714f3dc52696ab9832c4230131bb2d8a5020d79ed96b10df8cc0cdf81954f035809570e80692efeff5277ea7528a8fbc9bebf9fbbb7798e1805e180bd50349481d353c269a2d24ccf6cf4572c104a3ffb22fd8b97e2c95ba62bcdd61b6bdb687f4bc2a05034c005e58def2467ff9340cf2d62a2a050b1f13aa83dfd80d1f9b80522afc8354590588ee33a7cbd3e27"),
            D = Hex("7f76cd0ee2d4de051cc6d9a80e8dfa7bca1eaa271a40f8f1228735dddbfdeef8c2bcbd01fb8be23e63b2b1225c56496e11be07440b9a2666d1492c8fd31bcfa4a1b8d1fba49ed2212883098af6a00ba3d60f9b6368ccbc0c4e145b27a4a9f42bb9b87bc0e651ad1d77d46bb9ce20d126667e5e9ea2e96b90f373b8528f4411030c1397393d132258d5438249da6e7ca1c58ca5b009e0ce3ddff49d3c9715e26ac72b3c509323dbba4a226644ac78bb0e1a2743b57167aff4ab48469373d042ab9363e56c9ade5024c0237d99793f2207e0c148561bdf830912b42d456bc9c06885999079961ad7f54d1f3783404aec3937a680927dc580c7d66ffe8a7989c6b1"),
            P = Hex("fef6bf1d69ab16250847556b86e43588722ab13df8b644cab3ab19d10424280a7455b8154509cc131cf2ba37a903908f0210ff257986cc18509a105f5b4c1c4eb0a7e359b12da0c6b0202c213312b3af723483cd522faf0f205a1bc0e2a376340fd7fcc141c9f979401742213e9dfdc7c150de445ac931896a7805be65b4e82d"),
            Q = Hex("c79e4758007d6282b0d22281d4a8971b790c3ab0d7c930e3c3538e57eff09b9fb39052c6942236aae64a5f721d70e87658c8b291ce9cc3e9097f2e4797cc9039153531de1f0c8c0dc1c292be97bf2f91a18c7d50a8212fd7a29a7eb5a72a9002d9f33dd1ebb8e05a799e7d8dca186dbd9ea180286b2afe51249b6f4d84778023"),
            DP = Hex("6d48e0544025c84129524227ebd2c7ab6b9c270ab41f944efa421db7bcb9aebc046f758f105f89acab9cd2fae6a4138368d45638fee52b78449c34e65aa0be0570ad15c32d31ac975d88fcc1623de2ed11dbb69efc5a5a03f6cf08d45d90c92ab99bcfc81a65f35be87fcfa5a64c5c2a120f92a5e3f0171e9a974586fddb5425"),
            DQ = Hex("2a51ce02442850e830207c9c55bf6039bcd1f0e768f8085b611fa7bfd0e88bb5b1d5d916ac750c6df2e0b59775d268161f007d8b17e8784841712b18968011db68399cd6e0724286f01b160d3e12943d25a8a9309e545ad6366cd68c20628fa16b1f7c6db2b1c12ead36029c3aca2f09d2459eebf2bc6caa3b3e90bc3867354d"),
            InverseQ = Hex("0b671c0d6c57d3e7056594315655fd2808fa058acc5539619763a016273dedc116402a12ea6fd9d85856a8568b0d385e1e803b5f40806f624f28a269f3d3f7fdb2c3524320929d978da01507156ea40d56d3371ac49edf0249b80a8462f5fab93fa40976ccaa b99ba64fc16a64ced877ab4bf9a0aedaf167877c985c7eb873f5"),
            Exponent = Hex("010001"),
        },
    };

    /// <summary>Parses a compact hex string into a byte array.</summary>
    public static byte[] Hex(string hex)
    {
        hex = hex.Replace(" ", "");
        var data = new byte[hex.Length / 2];
        for (int i = 0; i < data.Length; i++)
            data[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return data;
    }

    /// <summary>
    /// Sanity check: verifies modulus = p * q and e * d ≡ 1 mod (p-1)(q-1)
    /// for a full RSA keypair. Used by the validation harness to catch
    /// transcription errors in the embedded constants.
    /// </summary>
    public static bool ValidateKeypair(RSAParameters k)
    {
        try
        {
            var n = new BigInteger(k.Modulus, isUnsigned: true, isBigEndian: true);
            var p = new BigInteger(k.P, isUnsigned: true, isBigEndian: true);
            var q = new BigInteger(k.Q, isUnsigned: true, isBigEndian: true);
            var e = new BigInteger(k.Exponent, isUnsigned: true, isBigEndian: true);
            var d = new BigInteger(k.D, isUnsigned: true, isBigEndian: true);
            if (n != p * q) return false;
            var phi = (p - 1) * (q - 1);
            if ((e * d) % phi != BigInteger.One) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
