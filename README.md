# DayZ Mod Workbench

Aplicativo C# para trabalhar com os projetos armazenados em:

`C:\Users\<usuario>\Desktop\edit mod`

A janela volta a abrir na última posição em que foi fechada. Enquanto ainda não existir esse histórico, ou se a posição salva estiver fora dos monitores atualmente conectados, ela abre centralizada na tela.

Os prefixos de compilação de cada projeto são armazenados em `configs\NomeDoProjeto\.dayzworkbench.ini`, junto ao Workbench, sem deixar arquivos de controle dentro da bancada. Configurações antigas encontradas na raiz de um projeto são migradas automaticamente para essa estrutura. Ao abrir o programa ou clicar em **Atualizar**, a pasta `configs` é sincronizada com a bancada e as configurações de projetos que não existem mais são removidas.

A aba **Extrair e compilar** acompanha o conteúdo disponível: com a bancada vazia, os quadros de extração e compilação ficam desativados; em um projeto existente, extrair só é liberado quando há um PBO selecionado e compilar só é liberado quando há uma pasta de source selecionada.

## Fluxo

1. Crie uma pasta de projeto, por exemplo `AutoCarFlip`.
2. Coloque os PBOs em `AutoCarFlip\PBO`.
3. No programa, selecione o projeto e use **Extrair PBO selecionado → source**.
4. Essa extração usa o mesmo pipeline de **Copiar + extrair PBOs** da Steam: recuperação pelo addon Python, auditorias, reconstrução ODOL e as duas barras de progresso, aplicadas somente ao PBO selecionado.
5. O Workbench converte automaticamente `config.bin` para `config.cpp` e materiais RaP para texto.
6. Edite os arquivos em `AutoCarFlip\source\NomeDoPBO`.
7. Use **PBO + BISIGN** para gerar o resultado em `AutoCarFlip\PBO`. Todos os sources são binarizados pelo Addon Builder; scripts, layouts e demais arquivos de runtime são incluídos diretamente e conferidos antes da saída ser aceita.
8. Use **Testar no DayZDiag** ou **Abrir com DayZ Editor**.

Na aba **Importar da Steam**, marque um ou mais mods instalados e escolha:

- **COPIAR PARA BANCADA** para copiar PBOs, BISIGN e metadados;
- **COPIAR + EXTRAIR PBOS** para também criar `source\NomeDoPBO` para cada PBO extraível e guardar `mod.cpp`/`meta.cpp` diretamente em `source`.

Durante a importação, o Workbench expande temporariamente a janela e mantém o painel de progresso fixo entre as abas e o registro das operações até o trabalho terminar. A barra superior mostra o progresso total de todos os PBOs e a cópia é medida pelos bytes transferidos. No modo de cópia e extração, a barra inferior tem o mesmo comprimento, permanece visível e acompanha somente a recuperação/verificação do PBO atual, zerando ao começar o próximo. O Python é iniciado com saída imediata para que cada modelo concluído atualize a verificação em tempo real; depois a barra continua pela validação dos relatórios até 100%. A recuperação interna de payloads não fornece contagens intermediárias ao Workbench e, somente nessa fase, a barra usa animação contínua em vez de exibir um percentual fictício; assim que o resultado retorna, ela volta ao percentual real. Os controles da janela ficam bloqueados durante a operação e todos os botões usam temporariamente o mesmo cinza; ao terminar, suas cores originais são restauradas. Esse acompanhamento é feito inteiramente pelo aplicativo C# e não modifica o addon Python.

O mesmo painel acompanha a compilação. A barra superior representa o progresso total de um ou de todos os sources; a inferior mostra o source atual passando por cópia, conversão dos configs, binarização, empacotamento, validação e assinatura. Etapas internas do Addon Builder que não informam percentual usam animação contínua até a próxima etapa mensurável.

A lista combina os atalhos de `DayZ\!Workshop` com as instalações em `steamapps\workshop\content\221100`, elimina duplicatas pelo Workshop ID e mostra `Nome do mod │ Workshop ID`. A pesquisa aceita tanto o nome quanto o ID completo ou parcial.

Cada mod recebe seu próprio projeto em `Desktop\edit mod`. Em operações que incluem extração, `mod.cpp` e `meta.cpp` ficam na raiz da pasta `source`; na cópia sem extração, permanecem na raiz do projeto. Ao preparar `_test`, o Workbench recoloca esses metadados na raiz do mod executável. Chaves públicas e privadas não são copiadas. Se o PBO estiver ofuscado, inclusive por MPG Packer, o Workbench usa o addon Python para reconstruir `config.cpp` e os scripts alcançáveis pelo grafo de includes. O PBO e o BISIGN originais permanecem preservados; se a recuperação não passar nas validações, nenhum source incompleto substitui o existente.

Ao escolher **Abrir com DayZ Editor**, o programa mostra duas listas com seleção múltipla:

- todos os mods instalados em `DayZ\!Workshop`;
- todos os projetos disponíveis em `Desktop\edit mod`.

CF, Dabs Framework, DayZ Editor e o projeto atual começam marcados. Projetos da bancada que possuem PBO compilado são preparados automaticamente na pasta `_test`.

O modo Editor usa o cliente normal `DayZ_x64.exe`. O botão **Testar no DayZDiag** permanece separado para diagnósticos. Antes de iniciar, o programa detecta instâncias antigas do DayZ e oferece encerrá-las para evitar processos invisíveis.

Na aba **Configurações**, o botão **Auto detectar** consulta as bibliotecas configuradas na Steam e preenche os caminhos do DayZ, DayZ Tools, Python, Workshop, DayZ Editor, CF e Dabs Framework. O Addon Builder e o work drive do DayZ Tools também podem ser configurados nessa aba. O conversor ODOL aparece como addon gerenciado pelo próprio Workbench. Bancada de mods, chaves públicas e pasta de backups continuam manuais.

As chaves privadas de assinatura ficam em `keys`, ao lado do executável. Na aba **Extrair e compilar**, o botão **Adicionar** importa uma `.biprivatekey` para essa pasta e passa a selecioná-la para assinatura. A chave configurada em versões anteriores é migrada automaticamente por cópia, sem apagar o arquivo original. A pasta `keys` é ignorada pelo Git e nenhuma chave privada é copiada para projetos, PBOs ou mods de teste.

## Source editável e limitações

O BankRev extrai os arquivos armazenados em PBOs não protegidos. Em seguida, o CfgConvert transforma `config.bin` em `config.cpp` e converte RVMAT, BISURF, SQM, FSM, BIKB, EXT, CPP e CFG rapificados para texto. O conversor ODOL configurado reconstrói modelos ODOL53, ODOL54 e ODOL55 como MLOD e recupera `model.cfg` a partir dos dados de esqueleto e animação incorporados. O botão **Desbinarizar source selecionado** aplica o mesmo processo a uma extração antiga.

O conversor Python é distribuído como **addon modular v8** do Workbench e fica em `addons\deodol_source_windows.py`, com o engine em `addons\deodol_engine`. Antes do primeiro uso de cada execução, o Workbench consulta o `manifest.json` publicado no repositório de atualizações, valida schema, API, versão mínima do Workbench, release, versão do engine e SHA-256 do pacote. Se a versão local for igual ou mais nova, nenhum pacote é baixado. Quando existe uma atualização válida, o pacote criptografado é baixado, validado, extraído em uma área temporária e instalado com rollback automático. Uma falha de rede ou de atualização nunca substitui uma instalação local válida.

Para extrair o pacote criptografado, o Workbench usa primeiro `tools\7zip\7z.exe`. Se ele não existir, procura uma instalação normal do 7-Zip e também o `PATH`. Caso nenhum 7-Zip esteja disponível, o próprio Workbench baixa automaticamente o `7zr.exe` oficial 26.02, valida seu SHA-256 e o salva como `tools\7zip\7z.exe` para reutilização. O arquivo `senha.txt` permanece local, ao lado do executável, e não é versionado pelo Git.

Em PBOs ofuscados compatíveis, o mesmo addon lê o arquivo diretamente, valida blocos comprimidos e o SHA-1 do arquivo, expande includes internos e recupera a árvore limpa de scripts. O Workbench exige relatório, manifesto, `config.cpp` ou `config.bin`, pelo menos um arquivo recuperado e zero erros de payload. Quando o BankRev fornece os modelos/texturas normalmente mas deixa um `config.cpp` vazio, o resultado do Python é mesclado à extração completa e o `config.bin` é novamente enviado ao CfgConvert. Includes não resolvidos aparecem como aviso para revisão antes da compilação.

Nesse fluxo complementar, o Python recupera somente os scripts e o config raiz; a source completa resulta da composição com a árvore integral extraída pelo BankRev. Antes da mesclagem, o Workbench compara individualmente o SHA-1 de todos os payloads extraídos com o manifesto produzido diretamente do PBO. Somente depois dessa auditoria converte configs internos e modelos. Relatórios transitórios do conversor são lidos pelo Workbench e não permanecem dentro da source nem entram no PBO recompilado.

Ao recompilar uma source reconstruída, o Workbench monta a estrutura do prefixo em uma pasta temporária isolada dentro de `%LocalAppData%\DayZ Mod Workbench` e a passa ao Addon Builder. Assim, os MLOD e o `model.cfg` voltam ao formato de runtime do jogo sem copiar a source para o work drive configurado (`P:\`) nem interferir no conteúdo existente nele. A pasta temporária de cada operação é removida ao terminar.

Na aba **Extrair e compilar**, o botão ao lado de **Verificar assinaturas** controla diretamente o WorkDrive do DayZ Tools. Ele mostra **Montar P:** quando a unidade está ausente e **Desmontar P:** quando o mapeamento configurado pelo DayZ Tools está ativo. Se a letra estiver ocupada por outro disco ou mapeamento, o Workbench não tenta desmontá-la.

O WorkDrive do DayZ Tools pode permanecer aguardando uma tecla mesmo depois de concluir a montagem. O Workbench reconhece a confirmação oficial, encerra esse processo auxiliar e libera a interface. Se não houver resposta em 20 segundos, a tentativa é encerrada e o programa orienta a conferir se a Steam está aberta e com o login ativo.

Se um ODOL não for versão 53, 54 ou 55, a reconstrução estiver incompleta ou o relatório indicar erro, o Workbench rejeita a saída reconstruída e preserva o modelo original. Um `config.cpp` vazio ou composto apenas por espaços não impede mais a conversão do `config.bin`. Configs e materiais RaP são lidos por um parser interno com validação estrita de tipos, limites, contagens e offsets; o texto reconstruído é recompilado apenas para validação antes de substituir o original. Assim, um arquivo defeituoso não prende o `CfgConvert`, não faz os demais arquivos serem ignorados e nunca é contado como convertido apenas porque uma ferramenta retornou código zero.

Ao final, a auditoria informa quantos arquivos RaP foram encontrados, convertidos, já possuíam fonte válido ou continuam binários. Quando o payload contém substituições UTF-8 `EF BF BD`, o addon v8 tenta primeiro a recuperação forense. Ele não aceita uma hipótese apenas porque o texto parece plausível: o RaP pré-corrupção reconstruído precisa reproduzir exatamente o payload danificado após a mesma transformação UTF-8 observada. Se essa prova não fechar e também não houver EmbeddedMaterial suficiente, o original é mantido e o resultado continua aparecendo como **SOURCE NÃO TOTALMENTE EDITÁVEL**. `texheaders.bin` é um índice/cache gerado das texturas PAA: o Workbench o ignora na source editável e o Addon Builder o recria no PBO final. O processo não cria `source_backups` nem `binary_backups`; durante uma substituição, usa somente uma pasta transitória para permitir reversão imediata em caso de erro e a remove ao concluir. Texturas PAA e áudios prontos para o jogo permanecem sem alteração. Tanto a reconstrução MLOD quanto a recuperação de scripts ofuscados devem ser validadas no Object Builder/DayZ antes da publicação; elas não equivalem ao projeto-fonte original do autor.

## Compilar o programa

Execute `build.bat`. O Visual Studio já instalado fornece o MSBuild necessário.

## Recuperação de materiais RVMAT

O addon v8 possui duas rotas complementares. Primeiro tenta **recuperação forense RaP** para arquivos que contêm a sequência UTF-8 `EF BF BD`: a árvore RaP é reconstruída ignorando offsets danificados e, quando necessário, valores numéricos de tamanho fixo são invertidos a partir da transformação UTF-8 observada. A recuperação só é aceita se o RaP pré-corrupção reconstruído, submetido novamente à mesma transformação de substituição UTF-8, reproduzir **byte por byte** o payload danificado do PBO. O sidecar `.rvmat.forensic.json` registra SHA-1, método e prova de round-trip.

Se a prova forense não fechar, o addon usa a rota baseada em `EmbeddedMaterial`: lê `EmbeddedMaterial` dos P3D ODOL e reconstrói o RVMAT quando o mapeamento compilado é unívoco, preservando um `.rvmat.embedded.json`. Ao final o Workbench executa novamente a auditoria RaP com `CfgConvert`; somente arquivos realmente convertidos deixam de aparecer como pendência.


## v7 final — SOURCE-PROOF

A recuperação de RVMAT agora combina a topologia RaP danificada sobrevivente com os valores compilados do ODOL EmbeddedMaterial. O RaP é a autoridade para ordem de classes/propriedades, gaps de Stage, presença/ausência de uvTransform e casing de strings. EmbeddedMaterial completa os valores semânticos perdidos.

Uma recuperação híbrida só recebe o estado SOURCE-PROOF quando o RaP pré-corrupção reconstruído, submetido à mesma transformação UTF-8 que produziu EF BF BD, reproduz byte por byte o payload danificado. No corpus SharpAxe_Bots_Extra validado contra a source real: 4 RVMATs por inversão forense direta + 23 por RaP+EmbeddedMaterial SOURCE-PROOF = 27/27, com 0 materiais apenas semânticos.
## v7.1.3 — fallback automático de config.bin e log persistente

Quando o BankRev extrai `config.bin` mas não produz um `config.cpp` textual utilizável (ausente, vazio ou inválido), o Workbench agora aciona automaticamente o addon Python v7 SOURCE-PROOF antes de declarar a source incompleta. Se o caso só for percebido pela pré-auditoria RaP, existe uma segunda tentativa automática pelo Python. Se o Python não resolver, o conversor RaP interno ainda é tentado e o `config.bin` original permanece preservado; a pendência só é exibida se todas as rotas falharem.

O painel **Registro das operações** também passou a ser persistente: todas as linhas exibidas na interface são gravadas em `workbench.log` na pasta do executável, com data/hora e categoria `[OP]`. Diagnósticos internos usam `[DIAG]`. Assim uma importação que apresente pendência pode ser analisada posteriormente mesmo depois de fechar o Workbench.


## Workbench 1.7.5 — PBO Tools marker recovery

O addon Python detecta genericamente PBOs do PBO Tools que preservem a sequência `deobfuscated_fileN.<ext>`. A entrada imediatamente seguinte é tratada como raiz confiável; para scripts, o caminho embaralhado é usado apenas para localizar o módulo DayZ e resolver/expandir o include graph. Decoys com GUIDs, extensões falsas, caracteres invisíveis e caminhos complexos ficam fora da source recuperada. O verificador mantém SHA-1/Cprs do arquivo inteiro e restringe validação semântica de assets aos alvos marcados, evitando falsos P3D criados por extensões-decoy.

O Workbench exige agora `PBO_TOOLS_MARKER_RECOVERY` e `PBO_FULL_PAYLOAD_RECOVERY` no addon, impedindo downgrade automático para uma revisão do addon que ainda não possua essas técnicas.
## Workbench 1.7.6 — PBO Tools 1.8.x marker-run recovery

O addon agora reconhece também a família observada em PBO Tools v1.8.1: três (ou mais, sem número fixo codificado) entradas consecutivas `deobfuscated_fileN` sem extensão funcionam como registros de controle; o primeiro arquivo não-marker imediatamente após o run é a raiz confiável. A sequência precisa ser numerada de forma contígua a partir de zero e é validada contra o banner/propriedades do PBO quando disponíveis. Isso evita selecionar milhares de decoys com extensões falsas.

No espécime `LS_Set_Redcore.pbo`: 103.717 entradas, 103.630 Cprs válidos, 103.458 decoys de comentário e 86 raízes confiáveis `0..85`, cada uma precedida por três markers. As raízes são exatamente 1 `config.bin`, 27 ODOL/P3D e 58 PAA. Os 27 modelos passam `SEMANTIC-EXACT`; em Windows, o `config.bin` segue para o CfgConvert oficial antes de o Workbench aceitar equivalência integral.

A capability `PBO_TOOLS_V18_MARKER_RECOVERY` passou a ser obrigatória no Workbench 1.7.6, impedindo downgrade automático para um addon que não possua suporte ao layout 1.8.x.

