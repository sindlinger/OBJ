using System;

namespace FilterPDF.Commands
{
    internal static class ObjectsLegend
    {
        public static void WriteLegend()
        {
            Console.WriteLine("Legenda (tipos comuns):");
            Console.WriteLine("- /XObject//Image: imagem embutida (scanner, carimbo, rubrica digitalizada).");
            Console.WriteLine("- (sem tipo)/texto: stream /Contents com texto (conteudo da pagina).");
            Console.WriteLine("- (sem tipo)/sem_texto: stream /Contents sem texto (so graficos/linhas).");
            Console.WriteLine("- (sem tipo)/stream: stream fora de /Contents (dado bruto/auxiliar).");
            Console.WriteLine("- (sem tipo)/dict: dicionario sem /Type (metadado ou auxiliar).");
            Console.WriteLine("- /Annot//Link: anotacao de link clicavel.");
            Console.WriteLine("- /Page: pagina individual (aponta /Contents e /Resources).");
            Console.WriteLine("- /Pages: arvore de paginas (Kids/Count).");
            Console.WriteLine("- /Catalog: raiz do documento.");
            Console.WriteLine("- /Font/*: fontes usadas no texto.");
            Console.WriteLine("- /ExtGState: estado grafico (opacidade/blend).");
            Console.WriteLine("- /Action: acao associada a links/outlines.");
            Console.WriteLine("- [ID]: numero interno do objeto no PDF (mostrado entre colchetes).");
            Console.WriteLine("- Use inspect streams show --id N ou inspect contents --obj N.");
            Console.WriteLine("- Colunas: Type=/Type e Subtype=/Subtype (quando ausentes, mostramos classificacao derivada).");
            Console.WriteLine();
        }
    }
}
