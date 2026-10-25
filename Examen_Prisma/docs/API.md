# API REST de ABCO

Base: `http://<servidor>:5080` en local, o `https://<dominio>` en la nube. JSON en camelCase.
Errores: `{"error": "mensaje en español"}` con el código HTTP correspondiente.
Rutas protegidas: cabecera `Authorization: Bearer <token>`.

## Salud
| Método | Ruta | Respuesta |
|---|---|---|
| GET | `/api/health` | `{"status":"ok","service":"ABCO","time":"…"}` |

## Autenticación (límite: 10 peticiones por minuto por IP → 429)
| Método | Ruta | Cuerpo | Respuesta |
|---|---|---|---|
| POST | `/api/auth/register` | JSON `{"username","password"}` (usuario `^[A-Za-z0-9_.-]{3,32}$`, contraseña ≥ 8) | 201 `{"id","username"}` · 400 · 409 si ya existe |
| POST | `/api/auth/login` | JSON `{"username","password"}` | 200 `{"token","username","role","twoFactorEnabled":false,"mustChangePassword"}` **o** 200 `{"requires2fa":true}` (sin token) · 401 |
| POST | `/api/auth/login/2fa` | multipart: `username`, `password`, `keyImage` (archivo PNG) | 200 `{"token","username","role","twoFactorEnabled":true,"mustChangePassword"}` · 401 `{"error"}` |

Si `mustChangePassword` es `true` (el administrador restableció la contraseña), el token solo sirve para `GET /api/account/me` y `POST /api/account/password`; cualquier otra ruta responde 403 `{"error"}` hasta que el usuario cambie la contraseña.

Flujo con 2FA: el cliente llama a `/login`. Si recibe `requires2fa: true`, pide la imagen-llave y envía **usuario + contraseña + imagen** a `/login/2fa`.

## Cuenta (requiere token)
| Método | Ruta | Cuerpo | Respuesta |
|---|---|---|---|
| GET | `/api/account/me` | — | `{"id","username","role","twoFactorEnabled","mustChangePassword","createdUtc"}` |
| POST | `/api/account/password` | JSON `{"currentPassword","newPassword"}` (nueva ≥ 8 y distinta) | 200 `{"twoFactorDisabled", "session":{"token","username","role","twoFactorEnabled","mustChangePassword":false}}` · 400 · 401. Si había 2FA, se desactiva (la llave dependía de la contraseña anterior). El cliente debe reemplazar su token por `session.token` |
| POST | `/api/account/2fa/enable` | multipart: `password` + (`catalogId` **o** archivo `image`) | 200 `image/png` (la imagen-llave; `Content-Disposition: attachment; filename=llave_<usuario>.png`) · 401 si la contraseña es incorrecta |
| POST | `/api/account/2fa/disable` | JSON `{"password"}` | 204 · 401 |

## Catálogo (público)
| Método | Ruta | Respuesta |
|---|---|---|
| GET | `/api/catalog` | `[{"id","name","width","height","prismaCapacity"}]`; ids: `atardecer, oceano, montanas, galaxia, mosaico, aurora` |
| GET | `/api/catalog/{id}` | `image/png` |

## Esteganografía (requiere token)
`mode` = `prisma` (ABCO, requiere `passphrase` ≥ 4) o `clasico` (esteganografía clásica: bits menos significativos en orden, sin clave). Las portadoras de más de 1280 px por lado se reducen.

| Método | Ruta | Cuerpo multipart | Respuesta |
|---|---|---|---|
| POST | `/api/stego/capacity` | `image` o `catalogId` | `{"originalWidth","originalHeight","width","height","resized","prismaCapacity","classicCapacity"}` |
| POST | `/api/stego/embed` | `image` o `catalogId`, `message`, `passphrase`, `mode` | 200 `image/png` + cabeceras `X-Prisma-Psnr`, `X-Prisma-Payload-Bytes`, `X-Prisma-Capacity`, `X-Prisma-Resized` · 413 si no cabe |
| POST | `/api/stego/extract` | `image`, `passphrase`, `mode` | 200 `{"mode","message","bytes"}` · 422 `{"error","rawSequentialHex"}` (lo que vería un atacante) |

## César (requiere token)
| Método | Ruta | Cuerpo JSON | Respuesta |
|---|---|---|---|
| POST | `/api/caesar/encrypt` | `{"text","key","enye":false}` | `{"result","alphabet"}` |
| POST | `/api/caesar/decrypt` | `{"text","key","enye":false}` | `{"result","alphabet"}` |
| POST | `/api/caesar/crack` | `{"text","enye":false}` | `{"best":{"key","chiSquare","preview"},"candidates":[…],"ms"}` |

## Comparador (requiere token)
| Método | Ruta | Cuerpo JSON | Respuesta |
|---|---|---|---|
| POST | `/api/compare` | `{"message","passphrase","caesarKey":3}` | `{"plainBytes","plainEntropy","results":[…]}` con 3 resultados (César, Esteganografía clásica, ABCO), cada uno `{"algorithm","kind","outputHex","outputBytes","ms","entropy","detectsTampering","notes"}` |

## Administración (rol `admin`)
| Método | Ruta | Respuesta |
|---|---|---|
| GET | `/api/admin/audit?take=100` | `[{"id","user"(ofuscado con César),"action","mode","bytes","ms","ok","utc"}]` |
| GET | `/api/admin/users` | `[{"id","username"(ofuscado),"role","twoFactorEnabled","mustChangePassword","createdUtc"}]` |
| POST | `/api/admin/users/{id}/reset-password` | 200 `{"temporaryPassword"}` (se muestra una sola vez). Desactiva el 2FA y obliga a cambiarla |
| POST | `/api/admin/users/{id}/role` | JSON `{"role":"admin"\|"user"}` → 200 `{"id","role"}` · 409 si es uno mismo o el último administrador |
| POST | `/api/admin/deobfuscate` | JSON `{"text"}` → `{"text"}` |
| POST | `/api/admin/users/{id}/reset-2fa` | 204 |
| GET | `/api/admin/trace/last` | `{"user","trace":{…},"text"}` (traza paso a paso de la última operación ABCO) |
| GET | `/api/admin/diffmap/last` | `image/png` (mapa \|original − estego\| amplificado) |
