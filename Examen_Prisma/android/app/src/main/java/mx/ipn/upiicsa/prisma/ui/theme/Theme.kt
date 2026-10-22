package mx.ipn.upiicsa.prisma.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.ui.graphics.Color

private val LightColors = lightColorScheme(
    primary = Violet40, onPrimary = Color.White,
    primaryContainer = Violet90, onPrimaryContainer = Violet10,
    secondary = Teal40, onSecondary = Color.White,
    secondaryContainer = Teal90, onSecondaryContainer = Teal20,
    tertiary = Amber40, onTertiary = Color.White,
    tertiaryContainer = Amber90, onTertiaryContainer = Amber20,
    error = Red40, onError = Color.White,
    errorContainer = Red90, onErrorContainer = Red20,
    background = Neutral98, onBackground = Neutral10,
    surface = Neutral98, onSurface = Neutral10,
    surfaceVariant = NeutralVariant90, onSurfaceVariant = NeutralVariant30,
    surfaceContainerLowest = Color.White,
    surfaceContainerLow = Neutral96,
    surfaceContainer = Neutral94,
    surfaceContainerHigh = Color(0xFFECE6EE),
    surfaceContainerHighest = Neutral90,
    outline = NeutralVariant50, outlineVariant = NeutralVariant80,
)

private val DarkColors = darkColorScheme(
    primary = Violet80, onPrimary = Violet30.copy(alpha = 1f),
    primaryContainer = Violet30, onPrimaryContainer = Violet90,
    secondary = Teal80, onSecondary = Teal20,
    secondaryContainer = Color(0xFF00504C), onSecondaryContainer = Teal90,
    tertiary = Amber80, onTertiary = Amber20,
    tertiaryContainer = Color(0xFF5E4100), onTertiaryContainer = Amber90,
    error = Red80, onError = Red20,
    errorContainer = Color(0xFF93000A), onErrorContainer = Red90,
    background = Neutral6, onBackground = Neutral90,
    surface = Neutral6, onSurface = Neutral90,
    surfaceVariant = NeutralVariant30, onSurfaceVariant = NeutralVariant80,
    surfaceContainerLowest = Color(0xFF0F0D13),
    surfaceContainerLow = Neutral10,
    surfaceContainer = Neutral12,
    surfaceContainerHigh = Neutral17,
    surfaceContainerHighest = Neutral22,
    outline = Color(0xFF938F99), outlineVariant = NeutralVariant30,
)

@Composable
fun PrismaTheme(darkTheme: Boolean = isSystemInDarkTheme(), content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = if (darkTheme) DarkColors else LightColors,
        typography = PrismaTypography,
        content = content,
    )
}

/** Verde de "sí" acorde al tema. */
val successColor: Color
    @Composable @ReadOnlyComposable
    get() = if (MaterialTheme.colorScheme.background == Neutral6) SuccessDark else Success
