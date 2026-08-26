# DayZ Mod Workbench

Aplicativo C# para trabalhar com os projetos armazenados em:

`C:\Users\Rafael-PC\Desktop\edit mod`

## Fluxo

1. Crie uma pasta de projeto, por exemplo `AutoCarFlip`.
2. Coloque os PBOs em `AutoCarFlip\PBO`.
3. No programa, selecione o projeto e use **Extrair PBO selecionado → source**.
4. Edite os arquivos em `AutoCarFlip\source\NomeDoPBO`.
5. Use **PBO + BISIGN** para gerar o resultado em `AutoCarFlip\PBO`.
6. Use **Testar no DayZDiag** ou **Abrir com DayZ Editor**.

Na aba **Importar da Steam**, marque um ou mais mods instalados e escolha:

- **COPIAR PARA BANCADA** para copiar PBOs, BISIGN e metadados;
- **COPIAR + EXTRAIR PBOS** para também criar `source\NomeDoPBO` para cada PBO extraível.

Cada mod recebe seu próprio projeto em `Desktop\edit mod`. Chaves públicas e privadas não são copiadas. Se o PBO estiver protegido/ofuscado, o programa conserva o PBO e o BISIGN e mostra um aviso, sem gerar um `source` ilegível.

Ao escolher **Abrir com DayZ Editor**, o programa mostra duas listas com seleção múltipla:

- todos os mods instalados em `DayZ\!Workshop`;
- todos os projetos disponíveis em `Desktop\edit mod`.

CF, Dabs Framework, DayZ Editor e o projeto atual começam marcados. Projetos da bancada que possuem PBO compilado são preparados automaticamente na pasta `_test`.

O modo Editor usa o cliente normal `DayZ_x64.exe`. O botão **Testar no DayZDiag** permanece separado para diagnósticos. Antes de iniciar, o programa detecta instâncias antigas do DayZ e oferece encerrá-las para evitar processos invisíveis.

A chave privada é usada diretamente de sua pasta segura e nunca é copiada para o projeto, PBO ou mod de teste.

## Limitação dos PBOs de terceiros

O BankRev extrai os arquivos armazenados em PBOs não protegidos. Arquivos que já foram publicados binarizados, como `config.bin` e modelos ODOL, continuam binarizados. A ferramenta não contorna proteção, ofuscação nem recupera automaticamente o fonte original.

## Compilar o programa

Execute `build.bat`. O Visual Studio já instalado fornece o MSBuild necessário.
