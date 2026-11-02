using System.Text;
using System.Xml.Linq;
using LogixTagRelay;
using LogixTagRelay.Design;

static class DescriptionTests
{
    public static void Run(Action<bool, string> check)
    {
        const string xml = """
        <RSLogix5000Content SoftwareRevision="35.03" CurrentLanguage="en-US">
          <Controller Name="Descriptions">
            <DataTypes>
              <DataType Name="Feedback"><Members>
                <Member Name="Ready" DataType="BOOL"><Description><![CDATA[Default ready feedback.]]></Description></Member>
              </Members></DataType>
              <DataType Name="Axis">
                <Description><![CDATA[Axis type default.]]></Description>
                <Members>
                  <Member Name="Target" DataType="DINT"><Description><![CDATA[Default target in millimetres.]]></Description></Member>
                  <Member Name="Feedback" DataType="Feedback"><Description><![CDATA[Axis feedback members.]]></Description></Member>
                </Members>
              </DataType>
            </DataTypes>
            <Tags>
              <Tag Name="Left" DataType="Axis"><Description><![CDATA[Left axis tag description.]]></Description>
                <Comments><Comment Operand=".tArGeT"><![CDATA[Left <command> & "quoted"
        </summary>
        public class Injected { }]]></Comment></Comments>
              </Tag>
              <Tag Name="Right" DataType="Axis"><Comments><Comment Operand=".Target"><![CDATA[Right-specific target.]]></Comment></Comments></Tag>
              <Tag Name="Matrix" DataType="Axis" Dimensions="2,2"><Comments>
                <Comment Operand="[1,0].Target"><![CDATA[Special matrix target.]]></Comment>
              </Comments></Tag>
              <Tag Name="Samples" DataType="DINT" Dimensions="2,2"><Description><![CDATA[Sample array.]]></Description><Comments>
                <Comment Operand="[1,1]"><![CDATA[Calibration sample.]]></Comment>
              </Comments></Tag>
              <Tag Name="NoComment" DataType="DINT" />
              <Tag Name="Localized" DataType="DINT"><Description>
                <LocalizedDescription Lang="de-DE">German description.</LocalizedDescription>
                <LocalizedDescription Lang="en-US">English description.</LocalizedDescription>
              </Description></Tag>
            </Tags>
            <Programs><Program Name="Main"><Tags>
              <Tag Name="Local" DataType="Axis"><Comments><Comment Operand=".Feedback.Ready"><![CDATA[Program-local ready override.]]></Comment></Comments></Tag>
            </Tags></Program></Programs>
          </Controller>
        </RSLogix5000Content>
        """;
        TagCatalog Read(string text) { using var input = new MemoryStream(Encoding.UTF8.GetBytes(text)); return L5xCatalog.Read(input); }
        var catalog = Read(xml);
        IEnumerable<TagNode> All(IEnumerable<TagNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(All(n.Children)));
        var byPath = All(catalog.Tags).ToDictionary(n => n.Path);
        check(byPath["Left"].Description == "Left axis tag description." && byPath["Right"].Description == "Axis type default.", "tag descriptions override data-type defaults");
        check(byPath["Left.Target"].Description!.StartsWith("Left <command>") && byPath["Right.Target"].Description == "Right-specific target.", "instance operand comments override UDT defaults case-insensitively");
        check(byPath["Matrix[0,0].Target"].Description == "Default target in millimetres." && byPath["Matrix[1,0].Target"].Description == "Special matrix target.", "multidimensional UDT elements retain defaults and exact indexed overrides");
        check(byPath["Left.Feedback.Ready"].Description == "Default ready feedback." && byPath["Program:Main.Local.Feedback.Ready"].Description == "Program-local ready override.", "nested and program-scoped operand descriptions resolve correctly");
        check(byPath["Samples[0,0]"].Description == "Sample array." && byPath["Samples[1,1]"].Description == "Calibration sample.", "primitive array comments override inherited array description");
        check(byPath["Localized"].Description == "English description." && byPath["NoComment"].Description is null, "localized export descriptions use CurrentLanguage with absent comments left empty");
        var selections = catalog.Tags.Select(n => new TagSelection(n.Path, Direction.Bidirectional)).ToArray();
        var bundle = Generator.Generate(catalog, selections);
        check(bundle.ModelCs.Contains("<summary>Left axis tag description.</summary>") && bundle.ModelCs.Contains("<summary>English description.</summary>"), "generated root IntelliSense uses source tag descriptions");
        check(bundle.ModelCs.Contains("Default target in millimetres.") && bundle.ModelCs.Contains("Right-specific target.") && bundle.ModelCs.Contains("Matrix[1,0].Target"), "shared UDT member docs list different instance comments with their paths");
        check(bundle.ModelCs.Contains("Calibration sample.") && bundle.ModelCs.Contains("Samples[1,1]"), "array property remarks expose element-specific comments");
        check(bundle.ModelCs.Contains("&lt;command&gt; &amp; &quot;quoted&quot;") && bundle.ModelCs.Contains("/// <para>&lt;/summary&gt;</para>") && !bundle.ModelCs.Contains("\npublic class Injected"), "multiline tag comments are escaped and cannot inject C# or XML documentation");
        var docs = bundle.ModelCs.Split('\n').Select(l => l.TrimStart()).Where(l => l.StartsWith("/// ")).Select(l => l[4..]);
        check(XDocument.Parse("<docs>" + string.Join("\n", docs) + "</docs>").Root is not null, "all generated descriptions form well-formed XML documentation");
        var project = new ContractProject("Comments", selections.ToList(), catalog);
        check(ContractProject.FromJson(project.ToJson()).Generate().ModelCs == bundle.ModelCs, "saved embedded catalogs preserve source descriptions on regeneration");
        TagNode Clear(TagNode node) => node with { Description = null, Children = node.Children.Select(Clear).ToList() };
        var plain = Generator.Generate(catalog with { Tags = catalog.Tags.Select(Clear).ToList() }, selections);
        check(plain.Configuration.ToJson() == bundle.Configuration.ToJson(), "descriptions do not change wire layout, hash or control settings");
        check(plain.ModelCs.Contains("PLC tag NoComment (Bidirectional)."), "undocumented members retain useful path and direction fallback descriptions");
        var unicode = Generator.Generate(Read(xml.Replace("Left axis tag description.", "Line one\u2028public class UnicodeInjection { }")), [new("Left", Direction.Bidirectional)]);
        check(unicode.ModelCs.Contains("/// <para>public class UnicodeInjection { }</para>") && !unicode.ModelCs.Contains("\u2028"), "Unicode line separators remain prefixed documentation text");
    }
}
