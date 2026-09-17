using System;
using System.Collections.Generic;
using System.Text;

namespace DevelApp.StepLexer
{
    /// <summary>
    /// Full Unicode ICU integration for normalization: uses the ICU-backed
    /// .NET normalization when the runtime provides it, and falls back to a
    /// self-contained managed implementation (covering the Latin, Greek and
    /// Cyrillic scripts) when the runtime executes in invariant-globalization
    /// mode without ICU data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The .NET runtime uses ICU for <see cref="string.Normalize(System.Text.NormalizationForm)"/>
    /// on most platforms, but in globalization-invariant mode (for example
    /// minimal containers or <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1</c>)
    /// normalization silently returns unchanged text instead of composing or
    /// decomposing characters. <see cref="IsFullNormalizationSupported"/>
    /// probes for that condition with a known composition pair.
    /// </para>
    /// <para>
    /// The managed fallback implements canonical decomposition, canonical
    /// ordering of combining marks, canonical composition and compatibility
    /// decomposition for the Latin, Greek and Cyrillic scripts (including
    /// Latin Extended Additional), plus common compatibility mappings such as
    /// fullwidth forms, ligatures and Roman numerals. Outside those ranges
    /// the fallback is best-effort: characters are passed through unchanged.
    /// </para>
    /// </remarks>
    public static class IcuUnicodeIntegration
    {
        /// <summary>
        /// Gets whether the runtime provides full ICU-backed normalization.
        /// When <see langword="false"/>, normalization requests are served by
        /// the managed fallback tables.
        /// </summary>
        public static bool IsFullNormalizationSupported { get; }

        /// <summary>Canonical decompositions (precomposed character to recursively decomposed base + combining marks).</summary>
        private const string CanonicalDecompositionData =
            "00C0=0041 0300;00C1=0041 0301;00C2=0041 0302;00C3=0041 0303;00C4=0041 0308;00C5=0041 030A;" +
            "00C7=0043 0327;00C8=0045 0300;00C9=0045 0301;00CA=0045 0302;00CB=0045 0308;00CC=0049 0300;" +
            "00CD=0049 0301;00CE=0049 0302;00CF=0049 0308;00D1=004E 0303;00D2=004F 0300;00D3=004F 0301;" +
            "00D4=004F 0302;00D5=004F 0303;00D6=004F 0308;00D9=0055 0300;00DA=0055 0301;00DB=0055 0302;" +
            "00DC=0055 0308;00DD=0059 0301;00E0=0061 0300;00E1=0061 0301;00E2=0061 0302;00E3=0061 0303;" +
            "00E4=0061 0308;00E5=0061 030A;00E7=0063 0327;00E8=0065 0300;00E9=0065 0301;00EA=0065 0302;" +
            "00EB=0065 0308;00EC=0069 0300;00ED=0069 0301;00EE=0069 0302;00EF=0069 0308;00F1=006E 0303;" +
            "00F2=006F 0300;00F3=006F 0301;00F4=006F 0302;00F5=006F 0303;00F6=006F 0308;00F9=0075 0300;" +
            "00FA=0075 0301;00FB=0075 0302;00FC=0075 0308;00FD=0079 0301;00FF=0079 0308;0100=0041 0304;" +
            "0101=0061 0304;0102=0041 0306;0103=0061 0306;0104=0041 0328;0105=0061 0328;0106=0043 0301;" +
            "0107=0063 0301;0108=0043 0302;0109=0063 0302;010A=0043 0307;010B=0063 0307;010C=0043 030C;" +
            "010D=0063 030C;010E=0044 030C;010F=0064 030C;0112=0045 0304;0113=0065 0304;0114=0045 0306;" +
            "0115=0065 0306;0116=0045 0307;0117=0065 0307;0118=0045 0328;0119=0065 0328;011A=0045 030C;" +
            "011B=0065 030C;011C=0047 0302;011D=0067 0302;011E=0047 0306;011F=0067 0306;0120=0047 0307;" +
            "0121=0067 0307;0122=0047 0327;0123=0067 0327;0124=0048 0302;0125=0068 0302;0128=0049 0303;" +
            "0129=0069 0303;012A=0049 0304;012B=0069 0304;012C=0049 0306;012D=0069 0306;012E=0049 0328;" +
            "012F=0069 0328;0130=0049 0307;0134=004A 0302;0135=006A 0302;0136=004B 0327;0137=006B 0327;" +
            "0139=004C 0301;013A=006C 0301;013B=004C 0327;013C=006C 0327;013D=004C 030C;013E=006C 030C;" +
            "0143=004E 0301;0144=006E 0301;0145=004E 0327;0146=006E 0327;0147=004E 030C;0148=006E 030C;" +
            "014C=004F 0304;014D=006F 0304;014E=004F 0306;014F=006F 0306;0150=004F 030B;0151=006F 030B;" +
            "0154=0052 0301;0155=0072 0301;0156=0052 0327;0157=0072 0327;0158=0052 030C;0159=0072 030C;" +
            "015A=0053 0301;015B=0073 0301;015C=0053 0302;015D=0073 0302;015E=0053 0327;015F=0073 0327;" +
            "0160=0053 030C;0161=0073 030C;0162=0054 0327;0163=0074 0327;0164=0054 030C;0165=0074 030C;" +
            "0168=0055 0303;0169=0075 0303;016A=0055 0304;016B=0075 0304;016C=0055 0306;016D=0075 0306;" +
            "016E=0055 030A;016F=0075 030A;0170=0055 030B;0171=0075 030B;0172=0055 0328;0173=0075 0328;" +
            "0174=0057 0302;0175=0077 0302;0176=0059 0302;0177=0079 0302;0178=0059 0308;0179=005A 0301;" +
            "017A=007A 0301;017B=005A 0307;017C=007A 0307;017D=005A 030C;017E=007A 030C;01A0=004F 031B;" +
            "01A1=006F 031B;01AF=0055 031B;01B0=0075 031B;01CD=0041 030C;01CE=0061 030C;01CF=0049 030C;" +
            "01D0=0069 030C;01D1=004F 030C;01D2=006F 030C;01D3=0055 030C;01D4=0075 030C;01D5=00DC 0304;" +
            "01D6=00FC 0304;01D7=00DC 0301;01D8=00FC 0301;01D9=00DC 030C;01DA=00FC 030C;01DB=00DC 0300;" +
            "01DC=00FC 0300;01DE=00C4 0304;01DF=00E4 0304;01E0=0226 0304;01E1=0227 0304;01E2=00C6 0304;" +
            "01E3=00E6 0304;01E6=0047 030C;01E7=0067 030C;01E8=004B 030C;01E9=006B 030C;01EA=004F 0328;" +
            "01EB=006F 0328;01EC=01EA 0304;01ED=01EB 0304;01EE=01B7 030C;01EF=0292 030C;01F0=006A 030C;" +
            "01F4=0047 0301;01F5=0067 0301;01F8=004E 0300;01F9=006E 0300;01FA=00C5 0301;01FB=00E5 0301;" +
            "01FC=00C6 0301;01FD=00E6 0301;01FE=00D8 0301;01FF=00F8 0301;0200=0041 030F;0201=0061 030F;" +
            "0202=0041 0311;0203=0061 0311;0204=0045 030F;0205=0065 030F;0206=0045 0311;0207=0065 0311;" +
            "0208=0049 030F;0209=0069 030F;020A=0049 0311;020B=0069 0311;020C=004F 030F;020D=006F 030F;" +
            "020E=004F 0311;020F=006F 0311;0210=0052 030F;0211=0072 030F;0212=0052 0311;0213=0072 0311;" +
            "0214=0055 030F;0215=0075 030F;0216=0055 0311;0217=0075 0311;0218=0053 0326;0219=0073 0326;" +
            "021A=0054 0326;021B=0074 0326;021E=0048 030C;021F=0068 030C;0226=0041 0307;0227=0061 0307;" +
            "0228=0045 0327;0229=0065 0327;022A=00D6 0304;022B=00F6 0304;022C=00D5 0304;022D=00F5 0304;" +
            "022E=004F 0307;022F=006F 0307;0230=022E 0304;0231=022F 0304;0232=0059 0304;0233=0079 0304;" +
            "0374=02B9;037E=003B;0385=00A8 0301;0386=0391 0301;0387=00B7;0388=0395 0301;" +
            "0389=0397 0301;038A=0399 0301;038C=039F 0301;038E=03A5 0301;038F=03A9 0301;0390=03CA 0301;" +
            "03AA=0399 0308;03AB=03A5 0308;03AC=03B1 0301;03AD=03B5 0301;03AE=03B7 0301;03AF=03B9 0301;" +
            "03B0=03CB 0301;03CA=03B9 0308;03CB=03C5 0308;03CC=03BF 0301;03CD=03C5 0301;03CE=03C9 0301;" +
            "03D3=03D2 0301;03D4=03D2 0308;0400=0415 0300;0401=0415 0308;0403=0413 0301;0407=0406 0308;" +
            "040C=041A 0301;040D=0418 0300;040E=0423 0306;0419=0418 0306;0439=0438 0306;0450=0435 0300;" +
            "0451=0435 0308;0453=0433 0301;0457=0456 0308;045C=043A 0301;045D=0438 0300;045E=0443 0306;" +
            "0476=0474 030F;0477=0475 030F;04C1=0416 0306;04C2=0436 0306;04D0=0410 0306;04D1=0430 0306;" +
            "04D2=0410 0308;04D3=0430 0308;04D6=0415 0306;04D7=0435 0306;04DA=04D8 0308;04DB=04D9 0308;" +
            "04DC=0416 0308;04DD=0436 0308;04DE=0417 0308;04DF=0437 0308;04E2=0418 0304;04E3=0438 0304;" +
            "04E4=0418 0308;04E5=0438 0308;04E6=041E 0308;04E7=043E 0308;04EA=04E8 0308;04EB=04E9 0308;" +
            "04EC=042D 0308;04ED=044D 0308;04EE=0423 0304;04EF=0443 0304;04F0=0423 0308;04F1=0443 0308;" +
            "04F2=0423 030B;04F3=0443 030B;04F4=0427 0308;04F5=0447 0308;04F8=042B 0308;04F9=044B 0308;" +
            "1E00=0041 0325;1E01=0061 0325;1E02=0042 0307;1E03=0062 0307;1E04=0042 0323;1E05=0062 0323;" +
            "1E06=0042 0331;1E07=0062 0331;1E08=00C7 0301;1E09=00E7 0301;1E0A=0044 0307;1E0B=0064 0307;" +
            "1E0C=0044 0323;1E0D=0064 0323;1E0E=0044 0331;1E0F=0064 0331;1E10=0044 0327;1E11=0064 0327;" +
            "1E12=0044 032D;1E13=0064 032D;1E14=0112 0300;1E15=0113 0300;1E16=0112 0301;1E17=0113 0301;" +
            "1E18=0045 032D;1E19=0065 032D;1E1A=0045 0330;1E1B=0065 0330;1E1C=0228 0306;1E1D=0229 0306;" +
            "1E1E=0046 0307;1E1F=0066 0307;1E20=0047 0304;1E21=0067 0304;1E22=0048 0307;1E23=0068 0307;" +
            "1E24=0048 0323;1E25=0068 0323;1E26=0048 0308;1E27=0068 0308;1E28=0048 0327;1E29=0068 0327;" +
            "1E2A=0048 032E;1E2B=0068 032E;1E2C=0049 0330;1E2D=0069 0330;1E2E=00CF 0301;1E2F=00EF 0301;" +
            "1E30=004B 0301;1E31=006B 0301;1E32=004B 0323;1E33=006B 0323;1E34=004B 0331;1E35=006B 0331;" +
            "1E36=004C 0323;1E37=006C 0323;1E38=1E36 0304;1E39=1E37 0304;1E3A=004C 0331;1E3B=006C 0331;" +
            "1E3C=004C 032D;1E3D=006C 032D;1E3E=004D 0301;1E3F=006D 0301;1E40=004D 0307;1E41=006D 0307;" +
            "1E42=004D 0323;1E43=006D 0323;1E44=004E 0307;1E45=006E 0307;1E46=004E 0323;1E47=006E 0323;" +
            "1E48=004E 0331;1E49=006E 0331;1E4A=004E 032D;1E4B=006E 032D;1E4C=00D5 0301;1E4D=00F5 0301;" +
            "1E4E=00D5 0308;1E4F=00F5 0308;1E50=014C 0300;1E51=014D 0300;1E52=014C 0301;1E53=014D 0301;" +
            "1E54=0050 0301;1E55=0070 0301;1E56=0050 0307;1E57=0070 0307;1E58=0052 0307;1E59=0072 0307;" +
            "1E5A=0052 0323;1E5B=0072 0323;1E5C=1E5A 0304;1E5D=1E5B 0304;1E5E=0052 0331;1E5F=0072 0331;" +
            "1E60=0053 0307;1E61=0073 0307;1E62=0053 0323;1E63=0073 0323;1E64=015A 0307;1E65=015B 0307;" +
            "1E66=0160 0307;1E67=0161 0307;1E68=1E62 0307;1E69=1E63 0307;1E6A=0054 0307;1E6B=0074 0307;" +
            "1E6C=0054 0323;1E6D=0074 0323;1E6E=0054 0331;1E6F=0074 0331;1E70=0054 032D;1E71=0074 032D;" +
            "1E72=0055 0324;1E73=0075 0324;1E74=0055 0330;1E75=0075 0330;1E76=0055 032D;1E77=0075 032D;" +
            "1E78=0168 0301;1E79=0169 0301;1E7A=016A 0308;1E7B=016B 0308;1E7C=0056 0303;1E7D=0076 0303;" +
            "1E7E=0056 0323;1E7F=0076 0323;1E80=0057 0300;1E81=0077 0300;1E82=0057 0301;1E83=0077 0301;" +
            "1E84=0057 0308;1E85=0077 0308;1E86=0057 0307;1E87=0077 0307;1E88=0057 0323;1E89=0077 0323;" +
            "1E8A=0058 0307;1E8B=0078 0307;1E8C=0058 0308;1E8D=0078 0308;1E8E=0059 0307;1E8F=0079 0307;" +
            "1E90=005A 0302;1E91=007A 0302;1E92=005A 0323;1E93=007A 0323;1E94=005A 0331;1E95=007A 0331;" +
            "1E96=0068 0331;1E97=0074 0308;1E98=0077 030A;1E99=0079 030A;1E9B=017F 0307;1EA0=0041 0323;" +
            "1EA1=0061 0323;1EA2=0041 0309;1EA3=0061 0309;1EA4=00C2 0301;1EA5=00E2 0301;1EA6=00C2 0300;" +
            "1EA7=00E2 0300;1EA8=00C2 0309;1EA9=00E2 0309;1EAA=00C2 0303;1EAB=00E2 0303;1EAC=1EA0 0302;" +
            "1EAD=1EA1 0302;1EAE=0102 0301;1EAF=0103 0301;1EB0=0102 0300;1EB1=0103 0300;1EB2=0102 0309;" +
            "1EB3=0103 0309;1EB4=0102 0303;1EB5=0103 0303;1EB6=1EA0 0306;1EB7=1EA1 0306;1EB8=0045 0323;" +
            "1EB9=0065 0323;1EBA=0045 0309;1EBB=0065 0309;1EBC=0045 0303;1EBD=0065 0303;1EBE=00CA 0301;" +
            "1EBF=00EA 0301;1EC0=00CA 0300;1EC1=00EA 0300;1EC2=00CA 0309;1EC3=00EA 0309;1EC4=00CA 0303;" +
            "1EC5=00EA 0303;1EC6=1EB8 0302;1EC7=1EB9 0302;1EC8=0049 0309;1EC9=0069 0309;1ECA=0049 0323;" +
            "1ECB=0069 0323;1ECC=004F 0323;1ECD=006F 0323;1ECE=004F 0309;1ECF=006F 0309;1ED0=00D4 0301;" +
            "1ED1=00F4 0301;1ED2=00D4 0300;1ED3=00F4 0300;1ED4=00D4 0309;1ED5=00F4 0309;1ED6=00D4 0303;" +
            "1ED7=00F4 0303;1ED8=1ECC 0302;1ED9=1ECD 0302;1EDA=01A0 0301;1EDB=01A1 0301;1EDC=01A0 0300;" +
            "1EDD=01A1 0300;1EDE=01A0 0309;1EDF=01A1 0309;1EE0=01A0 0303;1EE1=01A1 0303;1EE2=01A0 0323;" +
            "1EE3=01A1 0323;1EE4=0055 0323;1EE5=0075 0323;1EE6=0055 0309;1EE7=0075 0309;1EE8=01AF 0301;" +
            "1EE9=01B0 0301;1EEA=01AF 0300;1EEB=01B0 0300;1EEC=01AF 0309;1EED=01B0 0309;1EEE=01AF 0303;" +
            "1EEF=01B0 0303;1EF0=01AF 0323;1EF1=01B0 0323;1EF2=0059 0300;1EF3=0079 0300;1EF4=0059 0323;" +
            "1EF5=0079 0323;1EF6=0059 0309;1EF7=0079 0309;1EF8=0059 0303;1EF9=0079 0303"
;
        private const string CompatibilityDecompositionData =
            "00A0=0020;00AA=0061;00B2=0032;00B3=0033;00B9=0031;00BA=006F;" +
            "0132=0049 004A;0133=0069 006A;013F=004C 00B7;0140=006C 00B7;017F=0073;01C4=0044 017D;" +
            "01C5=0044 017E;01C6=0064 017E;01C7=004C 004A;01C8=004C 006A;01C9=006C 006A;01CA=004E 004A;" +
            "01CB=004E 006A;01CC=006E 006A;01F1=0044 005A;01F2=0044 007A;01F3=0064 007A;02B0=0068;" +
            "02B2=006A;02B3=0072;02B7=0077;02B8=0079;02E1=006C;02E2=0073;" +
            "02E3=0078;1D2C=0041;1D2D=00C6;1D2E=0042;1D30=0044;1D31=0045;" +
            "1D32=018E;1D33=0047;1D34=0048;1D35=0049;1D36=004A;1D37=004B;" +
            "1D38=004C;1D39=004D;1D3A=004E;1D3C=004F;1D3D=0222;1D3E=0050;" +
            "1D3F=0052;1D40=0054;1D41=0055;1D42=0057;1D43=0061;1D47=0062;" +
            "1D48=0064;1D49=0065;1D4D=0067;1D4F=006B;1D50=006D;1D51=014B;" +
            "1D52=006F;1D56=0070;1D57=0074;1D58=0075;1D5B=0076;1D62=0069;" +
            "1D63=0072;1D64=0075;1D65=0076;1D9C=0063;1D9E=00F0;1DA0=0066;" +
            "1DB5=01AB;1DBB=007A;2002=0020;2003=0020;2004=0020;2005=0020;" +
            "2006=0020;2007=0020;2008=0020;2009=0020;200A=0020;2024=002E;" +
            "2025=002E 002E;2026=002E 002E 002E;202F=0020;203C=0021 0021;2047=003F 003F;2048=003F 0021;" +
            "2049=0021 003F;205F=0020;2070=0030;2071=0069;2074=0034;2075=0035;" +
            "2076=0036;2077=0037;2078=0038;2079=0039;207A=002B;207C=003D;" +
            "207D=0028;207E=0029;207F=006E;2080=0030;2081=0031;2082=0032;" +
            "2083=0033;2084=0034;2085=0035;2086=0036;2087=0037;2088=0038;" +
            "2089=0039;208A=002B;208C=003D;208D=0028;208E=0029;2090=0061;" +
            "2091=0065;2092=006F;2093=0078;2095=0068;2096=006B;2097=006C;" +
            "2098=006D;2099=006E;209A=0070;209B=0073;209C=0074;20A8=0052 0073;" +
            "2100=0061 002F 0063;2101=0061 002F 0073;2102=0043;2103=00B0 0043;2105=0063 002F 006F;2106=0063 002F 0075;" +
            "2107=0190;2109=00B0 0046;210A=0067;210B=0048;210C=0048;210D=0048;" +
            "210E=0068;210F=0127;2110=0049;2111=0049;2112=004C;2113=006C;" +
            "2115=004E;2116=004E 006F;2119=0050;211A=0051;211B=0052;211C=0052;" +
            "211D=0052;2120=0053 004D;2121=0054 0045 004C;2122=0054 004D;2124=005A;2128=005A;" +
            "212C=0042;212D=0043;212F=0065;2130=0045;2131=0046;2133=004D;" +
            "2134=006F;2139=0069;213B=0046 0041 0058;2145=0044;2146=0064;2147=0065;" +
            "2148=0069;2149=006A;2160=0049;2161=0049 0049;2162=0049 0049 0049;2163=0049 0056;" +
            "2164=0056;2165=0056 0049;2166=0056 0049 0049;2167=0056 0049 0049 0049;2168=0049 0058;2169=0058;" +
            "216A=0058 0049;216B=0058 0049 0049;216C=004C;216D=0043;216E=0044;216F=004D;" +
            "2170=0069;2171=0069 0069;2172=0069 0069 0069;2173=0069 0076;2174=0076;2175=0076 0069;" +
            "2176=0076 0069 0069;2177=0076 0069 0069 0069;2178=0069 0078;2179=0078;217A=0078 0069;217B=0078 0069 0069;" +
            "217C=006C;217D=0063;217E=0064;217F=006D;2460=0031;2461=0032;" +
            "2462=0033;2463=0034;2464=0035;2465=0036;2466=0037;2467=0038;" +
            "2468=0039;2469=0031 0030;246A=0031 0031;246B=0031 0032;246C=0031 0033;246D=0031 0034;" +
            "246E=0031 0035;246F=0031 0036;2470=0031 0037;2471=0031 0038;2472=0031 0039;2473=0032 0030;" +
            "2474=0028 0031 0029;2475=0028 0032 0029;2476=0028 0033 0029;2477=0028 0034 0029;2478=0028 0035 0029;2479=0028 0036 0029;" +
            "247A=0028 0037 0029;247B=0028 0038 0029;247C=0028 0039 0029;247D=0028 0031 0030 0029;247E=0028 0031 0031 0029;247F=0028 0031 0032 0029;" +
            "2480=0028 0031 0033 0029;2481=0028 0031 0034 0029;2482=0028 0031 0035 0029;2483=0028 0031 0036 0029;2484=0028 0031 0037 0029;2485=0028 0031 0038 0029;" +
            "2486=0028 0031 0039 0029;2487=0028 0032 0030 0029;2488=0031 002E;2489=0032 002E;248A=0033 002E;248B=0034 002E;" +
            "248C=0035 002E;248D=0036 002E;248E=0037 002E;248F=0038 002E;2490=0039 002E;2491=0031 0030 002E;" +
            "2492=0031 0031 002E;2493=0031 0032 002E;2494=0031 0033 002E;2495=0031 0034 002E;2496=0031 0035 002E;2497=0031 0036 002E;" +
            "2498=0031 0037 002E;2499=0031 0038 002E;249A=0031 0039 002E;249B=0032 0030 002E;249C=0028 0061 0029;249D=0028 0062 0029;" +
            "249E=0028 0063 0029;249F=0028 0064 0029;24A0=0028 0065 0029;24A1=0028 0066 0029;24A2=0028 0067 0029;24A3=0028 0068 0029;" +
            "24A4=0028 0069 0029;24A5=0028 006A 0029;24A6=0028 006B 0029;24A7=0028 006C 0029;24A8=0028 006D 0029;24A9=0028 006E 0029;" +
            "24AA=0028 006F 0029;24AB=0028 0070 0029;24AC=0028 0071 0029;24AD=0028 0072 0029;24AE=0028 0073 0029;24AF=0028 0074 0029;" +
            "24B0=0028 0075 0029;24B1=0028 0076 0029;24B2=0028 0077 0029;24B3=0028 0078 0029;24B4=0028 0079 0029;24B5=0028 007A 0029;" +
            "24B6=0041;24B7=0042;24B8=0043;24B9=0044;24BA=0045;24BB=0046;" +
            "24BC=0047;24BD=0048;24BE=0049;24BF=004A;24C0=004B;24C1=004C;" +
            "24C2=004D;24C3=004E;24C4=004F;24C5=0050;24C6=0051;24C7=0052;" +
            "24C8=0053;24C9=0054;24CA=0055;24CB=0056;24CC=0057;24CD=0058;" +
            "24CE=0059;24CF=005A;24D0=0061;24D1=0062;24D2=0063;24D3=0064;" +
            "24D4=0065;24D5=0066;24D6=0067;24D7=0068;24D8=0069;24D9=006A;" +
            "24DA=006B;24DB=006C;24DC=006D;24DD=006E;24DE=006F;24DF=0070;" +
            "24E0=0071;24E1=0072;24E2=0073;24E3=0074;24E4=0075;24E5=0076;" +
            "24E6=0077;24E7=0078;24E8=0079;24E9=007A;24EA=0030;2A74=003A 003A 003D;" +
            "2A75=003D 003D;2A76=003D 003D 003D;2C7C=006A;2C7D=0056;3000=0020;3250=0050 0054 0045;" +
            "3251=0032 0031;3252=0032 0032;3253=0032 0033;3254=0032 0034;3255=0032 0035;3256=0032 0036;" +
            "3257=0032 0037;3258=0032 0038;3259=0032 0039;325A=0033 0030;325B=0033 0031;325C=0033 0032;" +
            "325D=0033 0033;325E=0033 0034;325F=0033 0035;32B1=0033 0036;32B2=0033 0037;32B3=0033 0038;" +
            "32B4=0033 0039;32B5=0034 0030;32B6=0034 0031;32B7=0034 0032;32B8=0034 0033;32B9=0034 0034;" +
            "32BA=0034 0035;32BB=0034 0036;32BC=0034 0037;32BD=0034 0038;32BE=0034 0039;32BF=0035 0030;" +
            "32CC=0048 0067;32CD=0065 0072 0067;32CE=0065 0056;32CF=004C 0054 0044;3371=0068 0050 0061;3372=0064 0061;" +
            "3373=0041 0055;3374=0062 0061 0072;3375=006F 0056;3376=0070 0063;3377=0064 006D;3378=0064 006D 00B2;" +
            "3379=0064 006D 00B3;337A=0049 0055;3380=0070 0041;3381=006E 0041;3383=006D 0041;3384=006B 0041;" +
            "3385=004B 0042;3386=004D 0042;3387=0047 0042;3388=0063 0061 006C;3389=006B 0063 0061 006C;338A=0070 0046;" +
            "338B=006E 0046;338E=006D 0067;338F=006B 0067;3390=0048 007A;3391=006B 0048 007A;3392=004D 0048 007A;" +
            "3393=0047 0048 007A;3394=0054 0048 007A;3399=0066 006D;339A=006E 006D;339C=006D 006D;339D=0063 006D;" +
            "339E=006B 006D;339F=006D 006D 00B2;33A0=0063 006D 00B2;33A1=006D 00B2;33A2=006B 006D 00B2;33A3=006D 006D 00B3;" +
            "33A4=0063 006D 00B3;33A5=006D 00B3;33A6=006B 006D 00B3;33A9=0050 0061;33AA=006B 0050 0061;33AB=004D 0050 0061;" +
            "33AC=0047 0050 0061;33AD=0072 0061 0064;33B0=0070 0073;33B1=006E 0073;33B3=006D 0073;33B4=0070 0056;" +
            "33B5=006E 0056;33B7=006D 0056;33B8=006B 0056;33B9=004D 0056;33BA=0070 0057;33BB=006E 0057;" +
            "33BD=006D 0057;33BE=006B 0057;33BF=004D 0057;33C2=0061 002E 006D 002E;33C3=0042 0071;33C4=0063 0063;" +
            "33C5=0063 0064;33C7=0043 006F 002E;33C8=0064 0042;33C9=0047 0079;33CA=0068 0061;33CB=0048 0050;" +
            "33CC=0069 006E;33CD=004B 004B;33CE=004B 004D;33CF=006B 0074;33D0=006C 006D;33D1=006C 006E;" +
            "33D2=006C 006F 0067;33D3=006C 0078;33D4=006D 0062;33D5=006D 0069 006C;33D6=006D 006F 006C;33D7=0050 0048;" +
            "33D8=0070 002E 006D 002E;33D9=0050 0050 004D;33DA=0050 0052;33DB=0073 0072;33DC=0053 0076;33DD=0057 0062;" +
            "33FF=0067 0061 006C;A7F2=0043;A7F3=0046;A7F4=0051;A7F8=0126;A7F9=0153;" +
            "FB00=0066 0066;FB01=0066 0069;FB02=0066 006C;FB03=0066 0066 0069;FB04=0066 0066 006C;FB05=017F 0074;" +
            "FB06=0073 0074;FB29=002B;FE10=002C;FE13=003A;FE14=003B;FE15=0021;" +
            "FE16=003F;FE33=005F;FE34=005F;FE35=0028;FE36=0029;FE37=007B;" +
            "FE38=007D;FE47=005B;FE48=005D;FE4D=005F;FE4E=005F;FE4F=005F;" +
            "FE50=002C;FE52=002E;FE54=003B;FE55=003A;FE56=003F;FE57=0021;" +
            "FE59=0028;FE5A=0029;FE5B=007B;FE5C=007D;FE5F=0023;FE60=0026;" +
            "FE61=002A;FE62=002B;FE63=002D;FE64=003C;FE65=003E;FE66=003D;" +
            "FE68=005C;FE69=0024;FE6A=0025;FE6B=0040;FF01=0021;FF02=0022;" +
            "FF03=0023;FF04=0024;FF05=0025;FF06=0026;FF07=0027;FF08=0028;" +
            "FF09=0029;FF0A=002A;FF0B=002B;FF0C=002C;FF0D=002D;FF0E=002E;" +
            "FF0F=002F;FF10=0030;FF11=0031;FF12=0032;FF13=0033;FF14=0034;" +
            "FF15=0035;FF16=0036;FF17=0037;FF18=0038;FF19=0039;FF1A=003A;" +
            "FF1B=003B;FF1C=003C;FF1D=003D;FF1E=003E;FF1F=003F;FF20=0040;" +
            "FF21=0041;FF22=0042;FF23=0043;FF24=0044;FF25=0045;FF26=0046;" +
            "FF27=0047;FF28=0048;FF29=0049;FF2A=004A;FF2B=004B;FF2C=004C;" +
            "FF2D=004D;FF2E=004E;FF2F=004F;FF30=0050;FF31=0051;FF32=0052;" +
            "FF33=0053;FF34=0054;FF35=0055;FF36=0056;FF37=0057;FF38=0058;" +
            "FF39=0059;FF3A=005A;FF3B=005B;FF3C=005C;FF3D=005D;FF3E=005E;" +
            "FF3F=005F;FF40=0060;FF41=0061;FF42=0062;FF43=0063;FF44=0064;" +
            "FF45=0065;FF46=0066;FF47=0067;FF48=0068;FF49=0069;FF4A=006A;" +
            "FF4B=006B;FF4C=006C;FF4D=006D;FF4E=006E;FF4F=006F;FF50=0070;" +
            "FF51=0071;FF52=0072;FF53=0073;FF54=0074;FF55=0075;FF56=0076;" +
            "FF57=0077;FF58=0078;FF59=0079;FF5A=007A;FF5B=007B;FF5C=007C;" +
            "FF5D=007D;FF5E=007E;FFE0=00A2;FFE1=00A3;FFE2=00AC;FFE3=00AF;" +
            "FFE4=00A6;FFE5=00A5"
;
        private const string CombiningClassData =
            "0300=230;0301=230;0302=230;0303=230;0304=230;0306=230;" +
            "0307=230;0308=230;0309=230;030A=230;030B=230;030C=230;" +
            "030F=230;0311=230;031B=216;0323=220;0324=220;0325=220;" +
            "0326=220;0327=202;0328=202;032D=220;032E=220;0330=220;" +
            "0331=220"
;

        /// <summary>Canonical decomposition table (precomposed char to decomposed sequence).</summary>
        private static readonly Dictionary<char, string> s_canonicalDecomposition = ParseEntries(CanonicalDecompositionData);

        /// <summary>Compatibility decomposition table (char to compatibility-decomposed sequence).</summary>
        private static readonly Dictionary<char, string> s_compatibilityDecomposition = ParseEntries(CompatibilityDecompositionData);

        /// <summary>Combining class table (combining mark to canonical combining class).</summary>
        private static readonly Dictionary<char, int> s_combiningClasses = ParseClasses(CombiningClassData);

        /// <summary>Canonical composition table ((base, mark) pair to precomposed character).</summary>
        private static readonly Dictionary<(char Base, char Mark), char> s_composition = BuildCompositionTable();

        /// <summary>
        /// Probe the runtime for full normalization support: a known
        /// combining sequence must compose and a known precomposed character
        /// must decompose.
        /// </summary>
        static IcuUnicodeIntegration()
        {
            bool supported;
            try
            {
                supported = "e\u0301".Normalize(System.Text.NormalizationForm.FormC) == "\u00e9"
                    && "\u00e9".Normalize(System.Text.NormalizationForm.FormD) == "e\u0301";
            }
            catch
            {
                supported = false;
            }

            IsFullNormalizationSupported = supported;
        }

        /// <summary>
        /// Normalize a string to the requested Unicode normalization form,
        /// using ICU when available and the managed fallback otherwise.
        /// </summary>
        /// <param name="input">The string to normalize.</param>
        /// <param name="form">The Unicode normalization form.</param>
        /// <returns>The normalized string. <see cref="UnicodeNormalizationForm.None"/> returns the input unchanged.</returns>
        public static string Normalize(string input, UnicodeNormalizationForm form)
        {
            if (input == null)
            {
                return string.Empty;
            }

            if (form == UnicodeNormalizationForm.None || input.Length == 0)
            {
                return input;
            }

            if (IsFullNormalizationSupported)
            {
                return form switch
                {
                    UnicodeNormalizationForm.NFC => input.Normalize(System.Text.NormalizationForm.FormC),
                    UnicodeNormalizationForm.NFD => input.Normalize(System.Text.NormalizationForm.FormD),
                    UnicodeNormalizationForm.NFKC => input.Normalize(System.Text.NormalizationForm.FormKC),
                    UnicodeNormalizationForm.NFKD => input.Normalize(System.Text.NormalizationForm.FormKD),
                    _ => input
                };
            }

            return NormalizeManaged(input, form);
        }

        /// <summary>
        /// Check whether two strings are canonically equivalent, i.e. whether
        /// they are equal after NFC normalization.
        /// </summary>
        /// <param name="first">The first string.</param>
        /// <param name="second">The second string.</param>
        /// <returns>True when the strings are canonically equivalent.</returns>
        public static bool AreCanonicallyEquivalent(string first, string second)
        {
            if (first == null || second == null)
            {
                return first == second;
            }

            return Normalize(first, UnicodeNormalizationForm.NFC) == Normalize(second, UnicodeNormalizationForm.NFC);
        }

        /// <summary>
        /// Managed fallback normalization: decompose (optionally with
        /// compatibility mappings), apply canonical ordering of combining
        /// marks and, for the composition forms, recompose.
        /// </summary>
        /// <param name="input">The string to normalize.</param>
        /// <param name="form">The Unicode normalization form.</param>
        /// <returns>The normalized string.</returns>
        private static string NormalizeManaged(string input, UnicodeNormalizationForm form)
        {
            bool compatibility = form == UnicodeNormalizationForm.NFKC || form == UnicodeNormalizationForm.NFKD;
            bool compose = form == UnicodeNormalizationForm.NFC || form == UnicodeNormalizationForm.NFKC;

            var decomposed = Decompose(input, compatibility);
            var ordered = CanonicalOrder(decomposed);
            return compose ? Compose(ordered) : ordered;
        }

        /// <summary>
        /// Expand every decomposable character to its (recursively expanded)
        /// decomposition, applying compatibility mappings when requested.
        /// </summary>
        /// <param name="input">The string to decompose.</param>
        /// <param name="compatibility">Whether compatibility mappings should be applied.</param>
        /// <returns>The fully decomposed string.</returns>
        private static string Decompose(string input, bool compatibility)
        {
            // Compatibility sequences may themselves contain decomposable
            // characters, so expansion runs to a fixed point (canonical
            // expansion is idempotent after one pass).
            string current = input;
            for (int pass = 0; pass < 4; pass++)
            {
                var sb = new StringBuilder(current.Length * 2);
                foreach (var c in current)
                {
                    if (compatibility && s_compatibilityDecomposition.TryGetValue(c, out var compatSequence))
                    {
                        sb.Append(compatSequence);
                    }
                    else if (s_canonicalDecomposition.TryGetValue(c, out var canonicalSequence))
                    {
                        sb.Append(canonicalSequence);
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }

                var next = sb.ToString();
                if (next == current)
                {
                    break;
                }

                current = next;
            }

            return current;
        }

        /// <summary>
        /// Apply canonical ordering: every maximal sequence of combining
        /// marks is stably sorted by canonical combining class.
        /// </summary>
        /// <param name="decomposed">A fully decomposed string.</param>
        /// <returns>The string with combining sequences in canonical order.</returns>
        private static string CanonicalOrder(string decomposed)
        {
            var sb = new StringBuilder(decomposed.Length);
            var sequence = new List<(char Character, int CombiningClass)>();
            void Flush()
            {
                if (sequence.Count > 1)
                {
                    sequence.Sort((a, b) => a.CombiningClass.CompareTo(b.CombiningClass));
                }

                foreach (var (character, _) in sequence)
                {
                    sb.Append(character);
                }

                sequence.Clear();
            }

            foreach (var c in decomposed)
            {
                var combiningClass = GetCombiningClass(c);
                if (combiningClass == 0)
                {
                    Flush();
                    sb.Append(c);
                }
                else
                {
                    sequence.Add((c, combiningClass));
                }
            }

            Flush();
            return sb.ToString();
        }

        /// <summary>
        /// Apply canonical composition: combining marks are merged into the
        /// preceding starter when a precomposed character exists and the
        /// composition is not blocked by an earlier mark of the same or
        /// higher combining class.
        /// </summary>
        /// <param name="ordered">A canonically ordered, decomposed string.</param>
        /// <returns>The composed string.</returns>
        private static string Compose(string ordered)
        {
            var sb = new StringBuilder(ordered.Length);
            int starter = -1;
            int lastClass = 0;

            foreach (var c in ordered)
            {
                var combiningClass = GetCombiningClass(c);
                if (starter >= 0
                    && (lastClass < combiningClass || lastClass == 0)
                    && s_composition.TryGetValue((sb[starter], c), out var composed))
                {
                    sb[starter] = composed;
                    continue;
                }

                if (combiningClass == 0)
                {
                    starter = sb.Length;
                }

                lastClass = combiningClass;
                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Get the canonical combining class of a character (0 for starters
        /// and unknown marks).
        /// </summary>
        /// <param name="c">The character.</param>
        /// <returns>The canonical combining class.</returns>
        private static int GetCombiningClass(char c)
        {
            return s_combiningClasses.TryGetValue(c, out var combiningClass) ? combiningClass : 0;
        }

        /// <summary>
        /// Build the composition table from the two-character canonical
        /// decompositions (base + single mark to precomposed character).
        /// </summary>
        /// <returns>The composition table.</returns>
        private static Dictionary<(char Base, char Mark), char> BuildCompositionTable()
        {
            var composition = new Dictionary<(char Base, char Mark), char>();
            foreach (var (precomposed, sequence) in s_canonicalDecomposition)
            {
                if (sequence.Length == 2 && GetCombiningClass(sequence[1]) != 0)
                {
                    composition[(sequence[0], sequence[1])] = precomposed;
                }
            }

            return composition;
        }

        /// <summary>
        /// Parse a table of entries of the form
        /// <c>XXXX=YYYY ZZZZ</c> into a character-to-sequence dictionary.
        /// </summary>
        /// <param name="data">The semicolon-separated table data.</param>
        /// <returns>The parsed dictionary.</returns>
        private static Dictionary<char, string> ParseEntries(string data)
        {
            var result = new Dictionary<char, string>();
            foreach (var entry in data.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split('=');
                if (parts.Length != 2)
                {
                    continue;
                }

                var sequence = new StringBuilder();
                foreach (var code in parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    sequence.Append((char)Convert.ToInt32(code, 16));
                }

                result[(char)Convert.ToInt32(parts[0], 16)] = sequence.ToString();
            }

            return result;
        }

        /// <summary>
        /// Parse a table of entries of the form <c>XXXX=NNN</c> into a
        /// character-to-combining-class dictionary.
        /// </summary>
        /// <param name="data">The semicolon-separated table data.</param>
        /// <returns>The parsed dictionary.</returns>
        private static Dictionary<char, int> ParseClasses(string data)
        {
            var result = new Dictionary<char, int>();
            foreach (var entry in data.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split('=');
                if (parts.Length == 2 && int.TryParse(parts[1], out var combiningClass))
                {
                    result[(char)Convert.ToInt32(parts[0], 16)] = combiningClass;
                }
            }

            return result;
        }
    }
}
