package mx.ipn.upiicsa.prisma.data

import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Assume.assumeTrue
import org.junit.Test

/**
 * Prueba de integración del cliente de ABCO contra un servidor real (por defecto http://localhost:5080).
 * Se omite si el servidor no responde. Para otro servidor, define la variable de entorno PRISMA_TEST_SERVER;
 * el administrador se toma de PRISMA_TEST_ADMIN / PRISMA_TEST_ADMIN_PASSWORD (por defecto admin / Admin#Prisma2026).
 *
 * Ojo: las rutas /api/auth admiten 10 peticiones por minuto por IP; entre ambas pruebas se hacen 8.
 */
class ApiClientIntegrationTest {
    private val base = System.getenv("PRISMA_TEST_SERVER") ?: "http://localhost:5080"
    private var token: String? = null
    private val api = ApiClient(baseUrl = { base }, token = { token })

    private fun serverUp(): Boolean = runCatching { runBlocking { api.health() }.status == "ok" }.getOrDefault(false)

    @Test
    fun flujoCompleto(): Unit = runBlocking {
        assumeTrue("Servidor no disponible en $base", serverUp())
        val user = "android_test_${System.currentTimeMillis() % 1_000_000}"
        val pass = "Prueba#2026x"

        // Registro y login simple
        assertEquals(user, api.register(user, pass).username)
        val login = api.login(user, pass)
        assertNotNull(login.token)
        assertTrue(!login.requires2fa)
        assertFalse(login.mustChangePassword)
        token = login.token

        // Sesión expirada / token inválido → SessionExpiredException
        val saved = token
        token = "invalido"
        try { api.me(); fail("Debió lanzar SessionExpiredException") } catch (_: SessionExpiredException) {}
        token = saved
        assertEquals(user, api.me().username)

        // Catálogo
        val catalog = api.catalog()
        assertTrue(catalog.isNotEmpty())
        val png = api.catalogImage(catalog.first().id)
        assertTrue(png.size > 100)

        // Etiquetas visibles de los modos (el valor de la API no cambia)
        assertEquals("ABCO", StegoMode.PRISMA.label)
        assertEquals("prisma", StegoMode.PRISMA.apiValue)
        assertEquals("Esteganografía clásica", StegoMode.CLASSIC.label)
        assertEquals("clasico", StegoMode.CLASSIC.apiValue)

        // Capacidad, ocultar y revelar (ABCO y esteganografía clásica)
        val cap = api.capacity(ApiClient.UploadSource.Catalog("oceano"))
        assertTrue(cap.prismaCapacity > 0 && cap.classicCapacity > 0)
        val embed = api.embed(ApiClient.UploadSource.Catalog("oceano"), "Hola ñandú", "frase1234", StegoMode.PRISMA)
        assertNotNull(embed.psnr)
        assertEquals("Hola ñandú".encodeToByteArray().size, embed.payloadBytes)
        val stego = LocalFile("stego.png", "image/png", embed.png)
        assertEquals("Hola ñandú", api.extract(stego, "frase1234", StegoMode.PRISMA).message)
        try {
            api.extract(stego, "otrafrase", StegoMode.PRISMA); fail("Debió fallar con 422")
        } catch (e: ApiException) {
            assertEquals(422, e.code)
            assertTrue(!e.rawSequentialHex.isNullOrBlank())
        }
        val classic = api.embed(ApiClient.UploadSource.File(LocalFile("c.png", "image/png", png)), "abc", "", StegoMode.CLASSIC)
        assertEquals("abc", api.extract(LocalFile("s.png", "image/png", classic.png), "", StegoMode.CLASSIC).message)

        // César y comparador
        val enc = api.caesarEncrypt("Hola", 3, true)
        assertEquals("Krñd", enc.result)
        assertEquals("Hola", api.caesarDecrypt(enc.result, 3, true).result)
        val crack = api.caesarCrack("Wkh txlfn eurzq ira mxpsv ryhu wkh odcb grj", false)
        assertEquals(3, crack.best.key)
        val cmp = api.compare("hola mundo", "frase1234", 3)
        assertEquals(3, cmp.results.size)
        assertEquals(listOf("César", "Esteganografía clásica", "ABCO"), cmp.results.map { it.algorithm })
        assertTrue(cmp.results.none { it.algorithm.contains("AES", ignoreCase = true) })

        // Un usuario normal no entra a administración
        try { api.adminUsers(); fail("Debió responder 403") } catch (e: ApiException) {
            assertTrue(e !is SessionExpiredException); assertEquals(403, e.code)
        }

        // 2FA: activar con catálogo, login en 2 pasos, contraseña incorrecta no expira sesión, desactivar
        try { api.enable2fa("mala", ApiClient.UploadSource.Catalog("aurora")); fail() } catch (e: ApiException) {
            assertTrue(e !is SessionExpiredException); assertEquals(401, e.code)
        }
        val key = api.enable2fa(pass, ApiClient.UploadSource.Catalog("aurora"))
        assertTrue(api.me().twoFactorEnabled)
        assertTrue(api.login(user, pass).requires2fa)
        val second = api.login2fa(user, pass, LocalFile("llave.png", "image/png", key))
        assertNotNull(second.token)
        token = second.token
        api.disable2fa(pass)
        assertTrue(!api.me().twoFactorEnabled)
    }

    /**
     * Restablecer contraseña (admin) → login con mustChangePassword → ruta bloqueada (403)
     * → cambiar contraseña → el token nuevo funciona. También rol (409) y desofuscar.
     */
    @Test
    fun restablecerContrasena(): Unit = runBlocking {
        assumeTrue("Servidor no disponible en $base", serverUp())
        val adminUser = System.getenv("PRISMA_TEST_ADMIN") ?: "admin"
        val adminPass = System.getenv("PRISMA_TEST_ADMIN_PASSWORD") ?: "Admin#Prisma2026"
        val user = "android_rst_${System.currentTimeMillis() % 1_000_000}"
        val pass = "Original#2026"
        val created = api.register(user, pass)

        // Administrador
        val adminLogin = api.login(adminUser, adminPass)
        assertNotNull("Login de admin (¿tiene 2FA?)", adminLogin.token)
        assertEquals("admin", adminLogin.role)
        token = adminLogin.token
        val target = api.adminUsers().first { it.id == created.id }
        assertTrue(target.username != user) // viene ofuscado
        assertEquals(user, api.deobfuscate(target.username))
        assertFalse(target.mustChangePassword)
        assertTrue(api.adminAudit(50).size <= 50)

        // Rol: quitarse el admin a uno mismo → 409 con mensaje; promover y degradar al usuario de prueba
        val self = api.adminUsers().first { api.deobfuscate(it.username) == adminUser }
        try { api.setUserRole(self.id, "user"); fail("Debió responder 409") } catch (e: ApiException) {
            assertEquals(409, e.code); assertTrue(e.message!!.isNotBlank())
        }
        assertEquals("admin", api.setUserRole(target.id, "admin").role)
        assertEquals("user", api.setUserRole(target.id, "user").role)

        // Restablecer contraseña
        val temp = api.resetUserPassword(target.id)
        assertTrue(temp.length >= 8)
        assertTrue(api.adminUsers().first { it.id == target.id }.mustChangePassword)

        // Login con la temporal: mustChangePassword y token restringido
        token = null
        val tempLogin = api.login(user, temp)
        assertTrue(tempLogin.mustChangePassword)
        token = tempLogin.token
        assertTrue(api.me().mustChangePassword)
        try { api.compare("hola", "frase1234", 3); fail("Debió responder 403") } catch (e: ApiException) {
            assertTrue(e !is SessionExpiredException); assertEquals(403, e.code)
        }

        // Validaciones del cambio: contraseña actual incorrecta no cierra la sesión
        try { api.changePassword("incorrecta", "NuevaClave#2026"); fail() } catch (e: ApiException) {
            assertTrue(e !is SessionExpiredException)
        }

        // Cambiar contraseña → token nuevo sin restricción
        val changed = api.changePassword(temp, "NuevaClave#2026")
        assertFalse(changed.session.mustChangePassword)
        assertTrue(changed.session.token.isNotBlank())
        assertEquals(user, changed.session.username)
        token = changed.session.token
        assertFalse(api.me().mustChangePassword)
        assertEquals(3, api.compare("hola", "frase1234", 3).results.size)

        // La nueva contraseña sirve para entrar
        token = null
        val again = api.login(user, "NuevaClave#2026")
        assertNotNull(again.token)
        assertFalse(again.mustChangePassword)
    }

    @Test
    fun errorDeRedLegible(): Unit = runBlocking {
        val offline = ApiClient(baseUrl = { "http://127.0.0.1:1" }, token = { null })
        try { offline.health(); fail() } catch (e: ApiException) {
            assertEquals(0, e.code)
            // Puerto cerrado: ConnectException → "El servidor rechazó la conexión…"; otros fallos de red → "No se pudo conectar…"
            val msg = e.message!!
            assertTrue(msg, msg.startsWith("El servidor rechazó la conexión") || msg.startsWith("No se pudo conectar"))
        }
    }
}
