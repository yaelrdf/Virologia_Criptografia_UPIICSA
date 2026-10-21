# API REST de PRISMA-L

Base: `http://<servidor>:5080` en local, o `https://<dominio>` en la nube. JSON en camelCase.
Errores: `{"error": "mensaje en español"}` con el código HTTP correspondiente.
Rutas protegidas: cabecera `Authorization: Bearer <token>`.

## Salud
| Método | Ruta | Respuesta |
|---|---|---|
| GET | `/api/health` | `{"status":"ok","service":"PRISMA-L","time":"…"}` |

## Autenticación (límite: 10 peticiones por minuto por IP → 429)
| Método | Ruta | Cuerpo | Respuesta |
|---|---|---|---|
| POST | `/api/auth/register` | JSON `{"username","password"}` (usuario `^[A-Za-z0-9_.-]{3,32}$`, contraseña ≥ 8) | 201 `{"id","username"}` · 400 · 409 si ya existe |
| POST | `/api/auth/login` | JSON `{"username","password"}` | 200 `{"token","username","role","twoFactorEnabled":false}` **o** 200 `{"requires2fa":true}` (sin token) · 401 |
| POST | `/api/auth/login/2fa` | multipart: `username`, `password`, `keyImage` (archivo PNG) | 200 `{"token","username","role","twoFactorEnabled":true}` · 401 `{"error"}` |

Flujo con 2FA: el cliente llama a `/login`. Si recibe `requires2fa: true`, pide la imagen-llave y envía **usuario + contraseña + imagen** a `/login/2fa`.

## Cuenta (requiere token)
| Método | Ruta | Cuerpo | Respuesta |
|---|---|---|---|
| GET | `/api/account/me` | — | `{"id","username","role","twoFactorEnabled","createdUtc"}` |
| POST | `/api/account/2fa/enable` | multipart: `password` + (`catalogId` **o** archivo `image`) | 200 `image/png` (la imagen-llave; `Content-Disposition: attachment; filename=llave_<usuario>.png`) · 401 si la contraseña es incorrecta |
| POST | `/api/account/2fa/disable` | JSON `{"password"}` | 204 · 401 |

## Catálogo (público)
| Método | Ruta | Respuesta |
|---|---|---|
| GET | `/api/catalog` | `[{"id","name","width","height","prismaCapacity"}]`; ids: `atardecer, oceano, montanas, galaxia, mosaico, aurora` |
| GET | `/api/catalog/{id}` | `image/png` |

## Esteganografía (requiere token)
`mode` = `prisma` (PRISMA-L, requiere `passphrase` ≥ 4) o `clasico` (LSB secuencial, sin clave). Las portadoras de más de 1280 px por lado se reducen.

| Método | Ruta | Cuerpo multipart | Respuesta |
|---|---|---|---|
| POST | `/api/stego/capacity` | `image` o `catalogId` | `{"originalWidth","originalHeight","width","height","resized","prismaCapacity","classicCapacity"}` |
| POST | `/api/stego/embed` | `image` o `catalogId`, `message`, `passphrase`, `mode` | 200 `image/png` + cabeceras `X-Prisma-Psnr`, `X-Prisma-Payload-Bytes`, `X-Prisma-Capacity`, `X-Prisma-Resized` · 413 si no cabe |
| POST | `/api/stego/extract` | `image`, `passphrase`, `mode` | 200 `{"mode","message","bytes"}` · 422 `{"error","rawSequentialHex"}` (lo que vería un atacante) |
| POST | `/api/stego/analyze` | `image` o `catalogId` | `{"width","height","chiSquare":[{"fraction","result":{"chiSquare","dof","embeddingProbability"}}]}` |

## César (requiere token)
| Método | Ruta | Cuerpo JSON | Respuesta |
|---|---|---|---|
| POST | `/api/caesar/encrypt` | `{"text","key","enye":false}` | `{"result","alphabet"}` |
| POST | `/api/caesar/decrypt` | `{"text","key","enye":false}` | `{"result","alphabet"}` |
| POST | `/api/caesar/crack` | `{"text","enye":false}` | `{"best":{"key","chiSquare","preview"},"candidates":[…],"ms"}` |

## Comparador (requiere token)
| Método | Ruta | Cuerpo JSON | Respuesta |
|---|---|---|---|
| POST | `/api/compare` | `{"message","passphrase","caesarKey":3}` | `{"plainBytes","plainEntropy","results":[{"algorithm","kind","outputHex","outputBytes","ms","entropy","detectsTampering","notes"}]}` |

## Administración (rol `admin`)
| Método | Ruta | Respuesta |
|---|---|---|
| GET | `/api/admin/audit?take=100` | `[{"id","user"(ofuscado con César),"action","mode","bytes","ms","ok","utc"}]` |
| GET | `/api/admin/users` | `[{"id","username"(ofuscado),"role","twoFactorEnabled","createdUtc"}]` |
| POST | `/api/admin/deobfuscate` | JSON `{"text"}` → `{"text"}` |
| POST | `/api/admin/users/{id}/reset-2fa` | 204 |
| GET | `/api/admin/trace/last` | `{"user","trace":{…},"text"}` (traza paso a paso de la última operación PRISMA-L) |
| GET | `/api/admin/diffmap/last` | `image/png` (mapa \|original − estego\| amplificado) |
