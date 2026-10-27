# Documentación de SignalTrace

Los capítulos están en Markdown y son la única fuente del documento Word de la entrega:

| Archivo | Contenido |
|---|---|
| `00-introduccion.md` | Objetivos, matriz de requisitos y tecnologías |
| `01-arquitectura.md` | Componentes, flujo, formato del sobre y del contenido |
| `02-algoritmos.md` | Cada algoritmo: para qué se usa, idea matemática, pasos, pseudocódigo, ejemplo numérico y archivo |
| `03-escenarios.md` | Escenarios normales y de ataque (pasivos y activos) con resultados |
| `04-android-y-api.md` | App Android (sistema híbrido) y API REST |
| `05-criptoanalisis-ycm1.md` | Por qué Yael Crypto March1 no es seguro solo; ataque de texto claro conocido |
| `06-matematicas.md` | Álgebra lineal, matemáticas discretas y métodos numéricos aplicados |
| `07-manual.md` | Instalación y uso |
| `08-limitaciones.md` | Modelo de amenazas, límites, lo que no se probó, ética |
| `09-pruebas.md` | Resultados de las pruebas |
| `10-conclusiones.md` | Conclusiones y referencias |

- `diagramas/*.dot`: diagramas de flujo en Graphviz. Para renderizarlos a PNG:

  ```bash
  for f in diagramas/*.dot; do dot -Tpng -Gdpi=150 "$f" -o "diagramas/png/$(basename "${f%.dot}").png"; done
  ```

- `capturas/`: capturas del sistema en ejecución. Las toma `tools/capturas.mjs` con puppeteer-core y el Firefox del sistema. Necesitan el servidor y el simulador corriendo:

  ```bash
  ST_PASSWORD='SignalTrace2026!' node tools/capturas.mjs http://localhost:5090
  ```

- `word/generar_documento.js`: genera `Documentacion_Sistema_SignalTrace.docx`.

  ```bash
  npm install
  ```

  ```bash
  node word/generar_documento.js salida.docx
  ```
