package mx.ipn.upiicsa.prisma.ui

import android.net.Uri
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.ImageBitmap
import mx.ipn.upiicsa.prisma.data.AccountInfo
import mx.ipn.upiicsa.prisma.data.CapacityResponse
import mx.ipn.upiicsa.prisma.data.CompareResponse
import mx.ipn.upiicsa.prisma.data.CrackResponse
import mx.ipn.upiicsa.prisma.data.EmbedResult
import mx.ipn.upiicsa.prisma.data.ExtractResponse
import mx.ipn.upiicsa.prisma.data.ImageSource
import mx.ipn.upiicsa.prisma.data.StegoMode

// Estado observable de cada pantalla. Vive en AppViewModel para sobrevivir a rotaciones
// y se reinicia al cerrar sesión (no deja mensajes ni llaves en memoria).

class LoginState {
    var username by mutableStateOf("")
    var password by mutableStateOf("")
    var confirm by mutableStateOf("")
    var registering by mutableStateOf(false)
    /** Paso 2 del login: el servidor pidió la imagen-llave. */
    var awaitingKey by mutableStateOf(false)
    var keyUri by mutableStateOf<Uri?>(null)
    var keyName by mutableStateOf("")
    var keyPreview by mutableStateOf<ImageBitmap?>(null)
    var loading by mutableStateOf(false)
    var error by mutableStateOf<String?>(null)
}

/** Imagen elegida (catálogo o galería) con su vista previa. */
class PickedImage {
    var source by mutableStateOf<ImageSource?>(null)
    var preview by mutableStateOf<ImageBitmap?>(null)
}

class HideState {
    val image = PickedImage()
    var message by mutableStateOf("")
    var passphrase by mutableStateOf("")
    var mode by mutableStateOf(StegoMode.PRISMA)
    var capacity by mutableStateOf<CapacityResponse?>(null)
    var capacityLoading by mutableStateOf(false)
    var loading by mutableStateOf(false)
    var result by mutableStateOf<EmbedResult?>(null)
    var resultPreview by mutableStateOf<ImageBitmap?>(null)
    var error by mutableStateOf<String?>(null)
}

class RevealState {
    var imageUri by mutableStateOf<Uri?>(null)
    var imageName by mutableStateOf("")
    var preview by mutableStateOf<ImageBitmap?>(null)
    var mode by mutableStateOf(StegoMode.PRISMA)
    var passphrase by mutableStateOf("")
    var loading by mutableStateOf(false)
    var result by mutableStateOf<ExtractResponse?>(null)
    var error by mutableStateOf<String?>(null)
    var rawHex by mutableStateOf<String?>(null)
}

class CaesarState {
    var text by mutableStateOf("")
    var key by mutableIntStateOf(3)
    var enye by mutableStateOf(false)
    var loading by mutableStateOf(false)
    var output by mutableStateOf<String?>(null)
    var outputLabel by mutableStateOf("")
    var alphabet by mutableStateOf("")
    var crack by mutableStateOf<CrackResponse?>(null)
    var error by mutableStateOf<String?>(null)

    val maxKey: Int get() = if (enye) 26 else 25
}

class CompareState {
    var message by mutableStateOf("")
    var passphrase by mutableStateOf("")
    var caesarKey by mutableIntStateOf(3)
    var loading by mutableStateOf(false)
    var result by mutableStateOf<CompareResponse?>(null)
    var error by mutableStateOf<String?>(null)
}

class AccountState {
    var info by mutableStateOf<AccountInfo?>(null)
    var loadingInfo by mutableStateOf(false)

    // Activar 2FA
    val keyImage = PickedImage()
    var enablePassword by mutableStateOf("")
    var enabling by mutableStateOf(false)
    var keyPng by mutableStateOf<ByteArray?>(null)
    var keyPreview by mutableStateOf<ImageBitmap?>(null)

    // Desactivar 2FA
    var disablePassword by mutableStateOf("")
    var disabling by mutableStateOf(false)

    var error by mutableStateOf<String?>(null)
}

/** Estado del diálogo de servidor. */
class ServerTestState {
    var testing by mutableStateOf(false)
    var ok by mutableStateOf<Boolean?>(null)
    var message by mutableStateOf<String?>(null)
}
