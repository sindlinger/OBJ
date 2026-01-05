using System;
using System.Collections.Generic;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FilterPDF.TjpbDespachoExtractor.Config
{
    public class TjpbDespachoConfig
    {
        public string Version { get; set; } = "2025-12-19";
        public string BaseDir { get; set; } = "";
        public ThresholdsConfig Thresholds { get; set; } = new ThresholdsConfig();
        public AnchorsConfig Anchors { get; set; } = new AnchorsConfig();
        public TemplateRegionsConfig TemplateRegions { get; set; } = new TemplateRegionsConfig();
        public DespachoTypeConfig DespachoType { get; set; } = new DespachoTypeConfig();
        public CertidaoConfig Certidao { get; set; } = new CertidaoConfig();
        public RegexConfig Regex { get; set; } = new RegexConfig();
        public PrioritiesConfig Priorities { get; set; } = new PrioritiesConfig();
        public FieldRulesConfig Fields { get; set; } = new FieldRulesConfig();
        public FieldStrategiesConfig FieldStrategies { get; set; } = new FieldStrategiesConfig();
        public ReferenceConfig Reference { get; set; } = new ReferenceConfig();

        public static TjpbDespachoConfig Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Config path is required", nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException("Config file not found", path);

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            using var reader = new StreamReader(path);
            var cfg = deserializer.Deserialize<TjpbDespachoConfig>(reader);
            var loaded = cfg ?? new TjpbDespachoConfig();
            loaded.BaseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            return loaded;
        }
    }

    public class ThresholdsConfig
    {
        public double BlankMaxPct { get; set; } = 15;
        public int MinPages { get; set; } = 2;
        public int MaxPages { get; set; } = 6;
        public BandsConfig Bands { get; set; } = new BandsConfig();
        public ParagraphConfig Paragraph { get; set; } = new ParagraphConfig();
        public MatchConfig Match { get; set; } = new MatchConfig();
    }

    public class BandsConfig
    {
        public double HeaderTopPct { get; set; } = 0.15;
        public double SubheaderPct { get; set; } = 0.15;
        public double BodyStartPct { get; set; } = 0.30;
        public double FooterBottomPct { get; set; } = 0.15;
    }

    public class ParagraphConfig
    {
        public double LineMergeY { get; set; } = 0.015;
        public double ParagraphGapY { get; set; } = 0.03;
        public double WordGapX { get; set; } = 0.012;
    }

    public class MatchConfig
    {
        public double DocScoreMin { get; set; } = 0.70;
        public int AnchorsMin { get; set; } = 3;
    }

    public class AnchorsConfig
    {
        public List<string> Header { get; set; } = new List<string>();
        public List<string> Subheader { get; set; } = new List<string>();
        public List<string> Title { get; set; } = new List<string>();
        public List<string> Footer { get; set; } = new List<string>();
        public List<string> SignerHints { get; set; } = new List<string>();
    }

    public class TemplateRegionsConfig
    {
        public RegionTemplateConfig FirstPageTop { get; set; } = new RegionTemplateConfig { MinY = 0.55, MaxY = 1.0 };
        public RegionTemplateConfig LastPageBottom { get; set; } = new RegionTemplateConfig { MinY = 0.0, MaxY = 0.45 };
        public RegionTemplateConfig CertidaoFull { get; set; } = new RegionTemplateConfig { MinY = 0.0, MaxY = 1.0 };
        public RegionTemplateConfig CertidaoValueDate { get; set; } = new RegionTemplateConfig { MinY = 0.0, MaxY = 1.0 };
        public double WordGapX { get; set; } = 0.012;
    }

    public class DespachoTypeConfig
    {
        public List<string> AutorizacaoHints { get; set; } = new List<string>();
        public List<string> GeorcHints { get; set; } = new List<string>();
        public List<string> ConselhoHints { get; set; } = new List<string>();
        public List<string> DeValuePatterns { get; set; } = new List<string>();
    }

    public class CertidaoConfig
    {
        public List<string> HeaderHints { get; set; } = new List<string>();
        public List<string> TitleHints { get; set; } = new List<string>();
        public List<string> BodyHints { get; set; } = new List<string>();
        public List<string> DateHints { get; set; } = new List<string>();
    }

    public class RegionTemplateConfig
    {
        public double MinY { get; set; } = 0.0;
        public double MaxY { get; set; } = 1.0;
        public List<string> Templates { get; set; } = new List<string>();
    }

    public class RegexConfig
    {
        public string ProcessoCnj { get; set; } = "";
        public string ProcessoSei { get; set; } = "";
        public string ProcessoAdme { get; set; } = "";
        public string Cpf { get; set; } = "";
        public string Money { get; set; } = "";
        public string DatePt { get; set; } = "";
        public string DateSlash { get; set; } = "";
    }

    public class PrioritiesConfig
    {
        public List<string> ProcessoAdminLabels { get; set; } = new List<string>();
        public List<string> PeritoLabels { get; set; } = new List<string>();
        public List<string> VaraLabels { get; set; } = new List<string>();
        public List<string> ComarcaLabels { get; set; } = new List<string>();
        public List<string> PromoventeLabels { get; set; } = new List<string>();
        public List<string> PromovidoLabels { get; set; } = new List<string>();
    }

    public class FieldRulesConfig
    {
        public FieldRuleConfig ProcessoAdministrativo { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig ProcessoJudicial { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Vara { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Comarca { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Promovente { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Promovido { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Perito { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig CpfPerito { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Especialidade { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig EspeciePericia { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig ValorJz { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig ValorDe { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig ValorCm { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig ValorTabela { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Adiantamento { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Percentual { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Parcela { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Data { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Assinante { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig NumPerito { get; set; } = new FieldRuleConfig();
    }

    public class FieldRuleConfig
    {
        public List<string> Templates { get; set; } = new List<string>();
        public List<string> Labels { get; set; } = new List<string>();
        public List<string> Hints { get; set; } = new List<string>();
    }

    public class ReferenceConfig
    {
        public List<string> PeritosCatalogPaths { get; set; } = new List<string>();
        public HonorariosConfig Honorarios { get; set; } = new HonorariosConfig();
    }

    public class HonorariosConfig
    {
        public string? TablePath { get; set; }
        public string? AliasesPath { get; set; }
        public List<HonorariosAreaMap> AreaMap { get; set; } = new List<HonorariosAreaMap>();
        public double ValueTolerancePct { get; set; } = 0.15;
        public bool PreferValorDe { get; set; } = true;
        public bool AllowValorJz { get; set; } = false;
    }

    public class HonorariosAreaMap
    {
        public string Area { get; set; } = "";
        public List<string> Keywords { get; set; } = new List<string>();
    }
}
