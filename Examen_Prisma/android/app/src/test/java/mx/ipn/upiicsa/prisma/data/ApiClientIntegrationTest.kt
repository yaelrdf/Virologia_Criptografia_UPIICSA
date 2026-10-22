package mx.ipn.upiicsa.prisma.data

import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Assume.assumeTrue
import org.junit.Test

/**
 * Prueba de integración del cliente contra un servidor real (por defecto http://localhost:5080).
 * Se omite si el servidor no responde. Para otro servidor, define la variable de entorno PRISMA_TEST_SERVER.
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

        // Capacidad, ocultar y revelar (PRISMA-L y clásico)
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
        assertTrue(cmp.results.size >= 3)

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

    @Test
    fun errorDeRedLegible(): Unit = runBlocking {
        val offline = ApiClient(baseUrl = { "http://127.0.0.1:1" }, token = { null })
        try { offline.health(); fail() } catch (e: ApiException) {
            assertEquals(0, e.code)
            assertTrue(e.message!!.startsWith("No se pudo conectar"))
        }
    }
}
