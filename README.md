# DayZ Mod Workbench

Aplicativo C# para trabalhar com os projetos armazenados em:

`C:\Users\Rafael-PC\Desktop\edit mod`

## Fluxo

1. Crie uma pasta de projeto, por exemplo `AutoCarFlip`.
2. Coloque os PBOs em `AutoCarFlip\PBO`.
3. No programa, selecione o projeto e use **Extrair PBO selecionado → source**.
4. O Workbench converte automaticamente `config.bin` para `config.cpp` e materiais RaP para texto.
5. Edite os arquivos em `AutoCarFlip\source\NomeDoPBO`.
6. Use **PBO + BISIGN** para gerar o resultado em `AutoCarFlip\PBO`. Sources com P3D MLOD são binarizadas pelo Addon Builder; as demais continuam usando FileBank.
7. Use **Testar no DayZDiag** ou **Abrir com DayZ Editor**.

Na aba **Importar da Steam**, marque um ou mais mods instalados e escolha:

- **COPIAR PARA BANCADA** para copiar PBOs, BISIGN e metadados;
- **COPIAR + EXTRAIR PBOS** para também criar `source\NomeDoPBO` para cada PBO extraível.

A lista combina os atalhos de `DayZ\!Workshop` com as instalações em `steamapps\workshop\content\221100`, elimina duplicatas pelo Workshop ID e mostra `Nome do mod │ Workshop ID`. A pesquisa aceita tanto o nome quanto o ID completo ou parcial.

Cada mod recebe seu próprio projeto em `Desktop\edit mod`. Chaves públicas e privadas não são copiadas. Se o PBO estiver ofuscado, inclusive por MPG Packer, o Workbench usa o addon Python para reconstruir `config.cpp` e os scripts alcançáveis pelo grafo de includes. O PBO e o BISIGN originais permanecem preservados; se a recuperação não passar nas validações, nenhum source incompleto substitui o existente.

Ao escolher **Abrir com DayZ Editor**, o programa mostra duas listas com seleção múltipla:

- todos os mods instalados em `DayZ\!Workshop`;
- todos os projetos disponíveis em `Desktop\edit mod`.

CF, Dabs Framework, DayZ Editor e o projeto atual começam marcados. Projetos da bancada que possuem PBO compilado são preparados automaticamente na pasta `_test`.

O modo Editor usa o cliente normal `DayZ_x64.exe`. O botão **Testar no DayZDiag** permanece separado para diagnósticos. Antes de iniciar, o programa detecta instâncias antigas do DayZ e oferece encerrá-las para evitar processos invisíveis.

Na aba **Configurações**, o botão **Auto detectar** consulta as bibliotecas configuradas na Steam e preenche os caminhos do DayZ, DayZ Tools, Python, Workshop, DayZ Editor, CF e Dabs Framework. O Addon Builder e o work drive do DayZ Tools também podem ser configurados nessa aba. O conversor ODOL aparece como addon gerenciado pelo próprio Workbench. Bancada de mods e chaves públicas continuam manuais.

As chaves privadas de assinatura ficam em `keys`, ao lado do executável. Na aba **Extrair e compilar**, o botão **Adicionar** importa uma `.biprivatekey` para essa pasta e passa a selecioná-la para assinatura. A chave configurada em versões anteriores é migrada automaticamente por cópia, sem apagar o arquivo original. A pasta `keys` é ignorada pelo Git e nenhuma chave privada é copiada para projetos, PBOs ou mods de teste.

## Source editável e limitações

O BankRev extrai os arquivos armazenados em PBOs não protegidos. Em seguida, o CfgConvert transforma `config.bin` em `config.cpp` e converte RVMAT, BISURF, SQM, FSM, BIKB, EXT, CPP e CFG rapificados para texto. O conversor ODOL configurado reconstrói modelos ODOL53, ODOL54 e ODOL55 como MLOD e recupera `model.cfg` a partir dos dados de esqueleto e animação incorporados. O botão **Desbinarizar source selecionado** aplica o mesmo processo a uma extração antiga.

O conversor Python v5 é tratado como addon do Workbench e fica em `addons\deodol_source_windows.py`, ao lado do executável. Antes do primeiro uso de cada execução, o programa consulta a [versão mais recente publicada no gist](https://gist.githubusercontent.com/RedSTwix/7dabd68cd538bc2d74b447829a1c7ea7/raw/deodol_source_windows.py), valida a estrutura do script e compara seu SHA-256 com o arquivo local. Se houver mudança, substitui o addon local de forma atômica. Se a internet estiver indisponível, mantém e usa a cópia local válida; instalações antigas em `P:\` ou `%LocalAppData%\DayZ Mod Workbench\tools` são migradas automaticamente. A integração passa o `CfgConvert.exe` oficial por `--cfgconvert` e só aceita o PBO quando o relatório v5 retorna `SEMANTIC-EXACT` ou `SEMANTIC-EXACT-INCLUDE-GRAPH`, com todos os P3D e configs verificados.

Em PBOs ofuscados compatíveis, o mesmo addon lê o arquivo diretamente, valida blocos comprimidos e o SHA-1 do arquivo, expande includes internos e recupera a árvore limpa de scripts. O Workbench exige relatório, manifesto, `config.cpp` ou `config.bin`, pelo menos um arquivo recuperado e zero erros de payload. Quando o BankRev fornece os modelos/texturas normalmente mas deixa um `config.cpp` vazio, o resultado do Python é mesclado à extração completa e o `config.bin` é novamente enviado ao CfgConvert. Includes não resolvidos aparecem como aviso para revisão antes da compilação.

Nesse fluxo complementar, o Python recupera somente os scripts e o config raiz; a source completa resulta da composição com a árvore integral extraída pelo BankRev. Antes da mesclagem, o Workbench compara individualmente o SHA-1 de todos os payloads extraídos com o manifesto produzido diretamente do PBO. Somente depois dessa auditoria converte configs internos e modelos. Relatórios transitórios do conversor são lidos pelo Workbench e não permanecem dentro da source nem entram no PBO recompilado.

Ao recompilar uma source reconstruída, o Workbench monta uma cópia temporária no work drive configurado (normalmente `P:\`) usando o prefixo original do PBO e chama o Addon Builder. Assim, os MLOD e o `model.cfg` voltam ao formato de runtime do jogo; a cópia temporária é removida ao terminar.

Na aba **Extrair e compilar**, o botão ao lado de **Verificar assinaturas** controla diretamente o WorkDrive do DayZ Tools. Ele mostra **Montar P:** quando a unidade está ausente e **Desmontar P:** quando o mapeamento configurado pelo DayZ Tools está ativo. Se a letra estiver ocupada por outro disco ou mapeamento, o Workbench não tenta desmontá-la.

Se um ODOL não for versão 53, 54 ou 55, a reconstrução estiver incompleta ou o relatório indicar erro, o Workbench rejeita a saída reconstruída e preserva o modelo original. Um `config.cpp` vazio ou composto apenas por espaços não impede mais a conversão do `config.bin`. Configs e materiais RaP são lidos por um parser interno com validação estrita de tipos, limites, contagens e offsets; o texto reconstruído é recompilado apenas para validação antes de substituir o original. Assim, um arquivo defeituoso não prende o `CfgConvert`, não faz os demais arquivos serem ignorados e nunca é contado como convertido apenas porque uma ferramenta retornou código zero.

Ao final, a auditoria informa quantos arquivos RaP foram encontrados, convertidos, já possuíam fonte válido ou continuam binários. Se bytes do arquivo já tiverem sido destruídos antes da extração — por exemplo, substituídos dentro do próprio PBO pelas sequências UTF-8 `EF BF BD` — o original é mantido e o resultado aparece explicitamente como **SOURCE NÃO TOTALMENTE EDITÁVEL**. Isso não significa que o arquivo ficou ausente: significa que ele foi preservado no formato do PBO, mas não pôde ser transformado em texto com exatidão. O Python não inventa os bytes perdidos. `texheaders.bin` é um índice/cache gerado das texturas PAA: o Workbench o ignora na source editável e o Addon Builder o recria no PBO final. O processo não cria pastas nem arquivos de backup. Durante uma substituição interna, usa somente uma pasta transitória para permitir reversão imediata em caso de erro e a remove ao concluir. Texturas PAA e áudios prontos para o jogo permanecem sem alteração. Tanto a reconstrução MLOD quanto a recuperação de scripts ofuscados devem ser validadas no Object Builder/DayZ antes da publicação; elas não equivalem ao projeto-fonte original do autor.

## Compilar o programa

Execute `build.bat`. O Visual Studio já instalado fornece o MSBuild necessário.
