# DayZ Mod Workbench

[![Build](https://github.com/RedSTwix/DayZ-Mod-Workbench/actions/workflows/release.yml/badge.svg)](https://github.com/RedSTwix/DayZ-Mod-Workbench/actions/workflows/release.yml)
[![Release](https://img.shields.io/github/v/release/RedSTwix/DayZ-Mod-Workbench)](https://github.com/RedSTwix/DayZ-Mod-Workbench/releases/latest)
[![Licença MIT](https://img.shields.io/badge/licen%C3%A7a-MIT-yellow.svg)](LICENSE)

Aplicativo gráfico para Windows que reúne os principais fluxos de trabalho com mods do DayZ: importação da Steam, extração e preparação de PBOs, recompilação, assinatura, montagem do WorkDrive e inicialização do DayZ ou do DayZ Editor.

> O Workbench integra ferramentas do DayZ Tools e um addon gerenciado para recuperação de conteúdo. Uma source reconstruída deve ser revisada no Object Builder e testada no DayZ antes de ser publicada.

## Recursos principais

- **Projetos organizados** — trabalha com projetos dentro de uma bancada configurável, usando as pastas `PBO` e `source`.
- **Importação da Steam** — localiza mods em `DayZ\!Workshop` e em `steamapps\workshop\content\221100`, exibe o Workshop ID, permite pesquisa e elimina duplicatas.
- **Extração de PBOs** — processa um ou vários PBOs em fila e mantém falhas isoladas sem interromper os demais itens.
- **Preparação de sources** — converte configurações e materiais RaP compatíveis para texto e preserva entradas que não passam pelas validações.
- **Recuperação assistida** — usa o addon Python gerenciado para PBOs protegidos ou ofuscados e para reconstruções ODOL suportadas pelo addon.
- **Compilação e assinatura** — usa Addon Builder, FileBank e DSSignFile conforme o conteúdo e as opções selecionadas.
- **Ambiente de teste** — prepara mods em `_test` e inicia o DayZDiag ou o DayZ Editor com os projetos e dependências escolhidos.
- **Drive P** — monta o WorkDrive e gerencia junctions explícitas entre `P:\NomeDaSource` e as sources da bancada.
- **Configuração local** — salva caminhos, argumentos de inicialização, posição da janela, configurações por projeto e registros de operação ao lado do aplicativo.

## Requisitos

- Windows.
- Runtime compatível com **.NET Framework 4.7.2**.
- DayZ instalado para iniciar o jogo ou o DayZDiag.
- DayZ Tools para BankRev, CfgConvert, Addon Builder, FileBank, DSSignFile e WorkDrive.
- Python configurado para os fluxos que utilizam o addon de recuperação.
- Steam aberta e autenticada quando uma operação do WorkDrive depender dela.

O Workbench pode localizar automaticamente instalações do DayZ e do DayZ Tools nas bibliotecas da Steam. Caminhos que não forem detectados podem ser informados na aba **Configurações**.

Para extrair pacotes do addon, o aplicativo procura uma cópia gerenciada, incluída, instalada ou disponível no `PATH`. Se nenhuma instalação do 7-Zip for encontrada, ele baixa o `7zr.exe` 26.02, valida o SHA-256 fixado no código e salva o executável em `tools\7zip\7z.exe`.

## Instalação

1. Acesse a página de [releases](https://github.com/RedSTwix/DayZ-Mod-Workbench/releases/latest).
2. Baixe o arquivo `DayZ-Mod-Workbench-vX.Y.Z.zip` da versão desejada.
3. Extraia o conteúdo para uma pasta com permissão de gravação.
4. Execute `DayZModWorkbench.exe`.
5. Abra **Configurações**, use **Auto detectar** e confira os caminhos necessários para o seu fluxo.

Os arquivos `settings.ini`, `configs`, `keys`, `addons`, `tools`, `editor files` e `workbench.log` são criados ou mantidos localmente conforme os recursos utilizados.

## Início rápido

A estrutura padrão de um projeto é:

```text
edit mod/
└── MeuProjeto/
    ├── PBO/
    └── source/
```

Fluxo básico:

1. Crie ou selecione um projeto na bancada.
2. Coloque os arquivos `.pbo` em `MeuProjeto\PBO` ou importe um mod pela aba **Importar da Steam**.
3. Selecione um ou mais PBOs e use **Extrair PBO selecionado → source**.
4. Revise e edite o conteúdo em `MeuProjeto\source\NomeDoPBO`.
5. Use **PBO + BISIGN** para recompilar e assinar o resultado.
6. Use **Testar no DayZDiag** ou **Abrir com DayZ Editor** para validar o mod.

## Importação da Steam

A aba **Importar da Steam** oferece dois modos:

- **Copiar para bancada** — copia PBOs, assinaturas e metadados para um projeto próprio.
- **Copiar + extrair PBOs** — além da cópia, prepara uma source para cada PBO extraível.

A lista combina os atalhos de `DayZ\!Workshop` com o conteúdo direto de `steamapps\workshop\content\221100`. Os mods são identificados pelo nome e, quando disponível, pelo Workshop ID.

Durante operações com vários arquivos, o painel de progresso separa o andamento geral do processamento do item atual. O registro permanece disponível em `workbench.log`.

## Extração e recuperação

O fluxo normal utiliza o BankRev para extrair o conteúdo do PBO. Em seguida, o Workbench:

1. converte `config.bin` e arquivos RaP compatíveis com o CfgConvert;
2. remove apenas caches regeneráveis reconhecidos;
3. executa auditorias sobre os arquivos extraídos e transformados;
4. tenta a recuperação complementar pelo addon gerenciado quando necessário;
5. preserva o PBO, os binários ou a source existente se as validações não forem satisfeitas.

O addon é instalado em `addons\deodol_source_windows.py`, com seu engine em `addons\deodol_engine`. O provisionador consulta o manifesto do repositório de atualizações, valida compatibilidade e integridade do pacote e mantém uma instalação local válida quando uma atualização falha.

### Limitações da reconstrução

- Nem todo PBO protegido ou ofuscado pode ser recuperado.
- Formatos e versões não reconhecidos permanecem preservados como binários.
- A reconstrução de modelos, materiais, configurações e scripts não equivale necessariamente ao projeto-fonte original do autor.
- O resultado deve ser revisado no Object Builder e validado no DayZ antes da distribuição.

## Compilação e assinatura

O Workbench monta um projeto temporário em `%LocalAppData%\DayZ Mod Workbench`, executa a ferramenta de compilação apropriada e verifica se o PBO gerado contém as entradas esperadas.

As chaves privadas importadas são armazenadas em `keys`, ao lado do executável. O aplicativo usa a chave local para assinar o resultado, mas não a copia para projetos, PBOs ou mods de teste. Arquivos `.biprivatekey` são ignorados pelo Git.

## DayZ, DayZDiag e DayZ Editor

- **Testar no DayZDiag** inicia `DayZDiag_x64.exe` com os argumentos configurados.
- **Abrir com DayZ Editor** permite selecionar mods instalados e projetos da bancada antes de iniciar `DayZ_x64.exe`.
- Projetos com PBO compilado podem ser preparados automaticamente na pasta `_test`.
- Os perfis usados pelo DayZ Editor ficam em `editor files\profiles` na pasta do Workbench.

Antes da inicialização, o aplicativo verifica processos antigos do DayZ e pode oferecer o encerramento dessas instâncias.

## Drive P e junctions

A aba **Drive P** centraliza a montagem do WorkDrive e os vínculos das sources.

- A ativação é sempre explícita; nenhuma junction é criada automaticamente.
- Cada vínculo usa o formato `P:\NomeDaSource` e aponta para a pasta original da source.
- Os registros ficam em `configs\workdrive-junctions.json`.
- A sincronização ocorre ao iniciar o aplicativo, abrir a aba, atualizar a bancada e montar o WorkDrive.
- Entradas não gerenciadas ou com destino divergente são preservadas.
- Sources com o mesmo nome são tratadas como conflito, pois disputariam o mesmo caminho no drive.

Se a letra configurada estiver ocupada por outro disco ou mapeamento, o Workbench bloqueia o gerenciamento e não altera o conteúdo existente.

## Configuração e dados locais

| Caminho | Finalidade |
|---|---|
| `settings.ini` | Caminhos das ferramentas, argumentos de inicialização e posição da janela |
| `configs\NomeDoProjeto\.dayzworkbench.ini` | Prefixo de compilação de cada projeto |
| `configs\workdrive-junctions.json` | Junctions gerenciadas no WorkDrive |
| `keys\` | Chaves privadas importadas localmente |
| `addons\` | Script e engine do addon gerenciado |
| `tools\7zip\` | Cópia gerenciada do 7-Zip, quando provisionada |
| `editor files\profiles\` | Perfis usados pelo DayZ Editor |
| `workbench.log` | Registro persistente das operações e diagnósticos |

A bancada padrão é `Desktop\edit mod`, mas pode ser alterada. Os caminhos detectados automaticamente devem ser revisados quando a Steam, o DayZ ou o DayZ Tools estiverem instalados em bibliotecas diferentes.

## Compilar o projeto

O projeto utiliza C# com Windows Forms e tem como alvo o .NET Framework 4.7.2.

Com o MSBuild disponível em um dos caminhos reconhecidos pelo script, execute:

```bat
build.bat
```

O script recompila `src\DayZModWorkbench.csproj` em modo Release e copia `DayZModWorkbench.exe` para a raiz do repositório.

O workflow [`build-and-release`](.github/workflows/release.yml) também compila o projeto no Windows em pushes para `main`, pull requests e execuções manuais. Tags `v*` compatíveis com a versão do assembly podem publicar uma release com pacote, checksum e atestado de procedência.

## Estrutura do repositório

```text
DayZ-Mod-Workbench/
├── .github/workflows/release.yml  # Build e publicação de releases
├── src/                           # Aplicação Windows Forms e integrações
├── build.bat                      # Build local com MSBuild
├── README.md                      # Documentação do projeto
└── LICENSE                        # Licença MIT
```

## Releases

Versões publicadas, pacotes e notas de alteração estão disponíveis na página de [releases](https://github.com/RedSTwix/DayZ-Mod-Workbench/releases).

O arquivo `SHA256SUMS.txt`, quando anexado pela automação de release, permite conferir a integridade do pacote distribuído.

## Licença

Este projeto é distribuído sob a [licença MIT](LICENSE).
