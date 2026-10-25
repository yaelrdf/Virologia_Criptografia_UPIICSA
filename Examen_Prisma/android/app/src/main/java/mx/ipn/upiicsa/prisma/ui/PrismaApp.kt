package mx.ipn.upiicsa.prisma.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.consumeWindowInsets
import androidx.compose.foundation.layout.displayCutout
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.systemBars
import androidx.compose.foundation.layout.union
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.BasicText
import androidx.compose.foundation.text.TextAutoSize
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.AccountCircle
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.Icon
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.LocalTextStyle
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationRail
import androidx.compose.material3.NavigationRailItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.path
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import mx.ipn.upiicsa.prisma.AppViewModel
import mx.ipn.upiicsa.prisma.ui.components.LocalWideLayout
import mx.ipn.upiicsa.prisma.ui.screens.AccountScreen
import mx.ipn.upiicsa.prisma.ui.screens.AdminScreen
import mx.ipn.upiicsa.prisma.ui.screens.CaesarScreen
import mx.ipn.upiicsa.prisma.ui.screens.CompareScreen
import mx.ipn.upiicsa.prisma.ui.screens.ForcedPasswordScreen
import mx.ipn.upiicsa.prisma.ui.screens.HideScreen
import mx.ipn.upiicsa.prisma.ui.screens.LoginScreen
import mx.ipn.upiicsa.prisma.ui.screens.RevealScreen

/** Escudo (Material "Shield"); material-icons-core no lo incluye. */
val ShieldIcon: ImageVector by lazy {
    ImageVector.Builder("Escudo", 24.dp, 24.dp, 24f, 24f).apply {
        path(fill = SolidColor(Color.Black)) {
            moveTo(12f, 1f)
            lineTo(3f, 5f)
            verticalLineToRelative(6f)
            curveToRelative(0f, 5.55f, 3.84f, 10.74f, 9f, 12f)
            curveToRelative(5.16f, -1.26f, 9f, -6.45f, 9f, -12f)
            lineTo(21f, 5f)
            lineToRelative(-9f, -4f)
            close()
        }
    }.build()
}

enum class Destination(val label: String, val icon: ImageVector, val adminOnly: Boolean = false) {
    HIDE("Ocultar", Icons.Filled.Lock),
    REVEAL("Revelar", Icons.Filled.Search),
    CAESAR("César", Icons.Filled.Refresh),
    COMPARE("Comparador", Icons.AutoMirrored.Filled.List),
    ACCOUNT("Cuenta", Icons.Filled.AccountCircle),
    ADMIN("Admin", ShieldIcon, adminOnly = true),
}

/** Ancho de ventana desde el que se usa NavigationRail (y diseños de 2 columnas). */
private val RailMinWidth: Dp = 600.dp

/** Con menos altura, el rail muestra la etiqueta solo en el ítem seleccionado. */
private val RailCompactHeight: Dp = 480.dp

@Composable
fun PrismaApp(vm: AppViewModel) {
    val snackbar = remember { SnackbarHostState() }
    LaunchedEffect(Unit) { vm.notices.collect { snackbar.showSnackbar(it) } }

    BoxWithConstraints(Modifier.fillMaxSize()) {
        val wide = maxWidth >= RailMinWidth
        val compactHeight = maxHeight < RailCompactHeight
        CompositionLocalProvider(LocalWideLayout provides wide) {
            Scaffold(
                snackbarHost = { SnackbarHost(snackbar) },
                // Barras del sistema y recorte de pantalla (cámara) en horizontal.
                contentWindowInsets = WindowInsets.systemBars.union(WindowInsets.displayCutout),
            ) { padding ->
                // consumeWindowInsets: el imePadding() de cada pantalla descuenta lo ya aplicado aquí.
                Box(Modifier.padding(padding).consumeWindowInsets(padding).fillMaxSize()) {
                    val session = vm.session
                    when {
                        session == null -> LoginScreen(vm)
                        session.mustChangePassword -> ForcedPasswordScreen(vm)
                        else -> MainShell(vm, wide, compactHeight, isAdmin = session.role == "admin")
                    }
                }
            }
        }
    }
}

/**
 * Navegación principal.
 *
 * Decisión de responsividad: el umbral es el ancho de la VENTANA (600 dp, el "compact" de Material).
 * - < 600 dp (teléfono vertical): barra inferior; con 6 destinos (admin) solo el seleccionado muestra
 *   etiqueta y las etiquetas se reducen solas para no truncarse en 360 dp.
 * - ≥ 600 dp (teléfono horizontal, tableta): NavigationRail. Sus ítems van en una columna con
 *   verticalScroll, así en horizontal (≈ 330 dp útiles) nunca se enciman ni se pierde el último.
 *   Si la altura es < 480 dp, solo el ítem seleccionado lleva etiqueta (ítems más bajos).
 *   Sin encabezado ni botón de salir: cerrar sesión está en Cuenta.
 */
@Composable
private fun MainShell(vm: AppViewModel, wide: Boolean, compactHeight: Boolean, isAdmin: Boolean) {
    var current by rememberSaveable { mutableStateOf(Destination.HIDE) }
    val destinations = Destination.entries.filter { !it.adminOnly || isAdmin }
    // Si se pierde el rol de admin estando en esa pantalla, vuelve al inicio.
    val shown = if (current in destinations) current else Destination.HIDE

    if (wide) {
        Row(Modifier.fillMaxSize()) {
            NavigationRail(windowInsets = WindowInsets(0)) {
                Column(
                    Modifier.fillMaxHeight().verticalScroll(rememberScrollState()).padding(vertical = 8.dp),
                    verticalArrangement = Arrangement.spacedBy(if (compactHeight) 0.dp else 4.dp, Alignment.CenterVertically),
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    destinations.forEach { d ->
                        NavigationRailItem(
                            selected = shown == d,
                            onClick = { current = d },
                            icon = { Icon(d.icon, contentDescription = d.label) },
                            label = { Text(d.label, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                            alwaysShowLabel = !compactHeight,
                        )
                    }
                }
            }
            Box(Modifier.weight(1f).fillMaxSize()) { DestinationContent(vm, shown) }
        }
    } else {
        val showAllLabels = destinations.size <= 5
        Scaffold(
            bottomBar = {
                NavigationBar(windowInsets = WindowInsets(0)) {
                    destinations.forEach { d ->
                        NavigationBarItem(
                            selected = shown == d,
                            onClick = { current = d },
                            icon = { Icon(d.icon, contentDescription = d.label) },
                            label = { NavLabel(d.label) },
                            alwaysShowLabel = showAllLabels,
                        )
                    }
                }
            },
            contentWindowInsets = WindowInsets(0),
        ) { padding ->
            Box(Modifier.padding(padding).consumeWindowInsets(padding).fillMaxSize()) {
                DestinationContent(vm, shown)
            }
        }
    }
}

/** Etiqueta de la barra inferior que reduce su tamaño (12 → 9 sp) si no cabe, en vez de truncarse. */
@Composable
private fun NavLabel(text: String) {
    BasicText(
        text = text,
        style = LocalTextStyle.current.copy(color = LocalContentColor.current),
        maxLines = 1,
        overflow = TextOverflow.Ellipsis,
        autoSize = TextAutoSize.StepBased(minFontSize = 9.sp, maxFontSize = 12.sp, stepSize = 0.5.sp),
    )
}

@Composable
private fun DestinationContent(vm: AppViewModel, destination: Destination) {
    when (destination) {
        Destination.HIDE -> HideScreen(vm)
        Destination.REVEAL -> RevealScreen(vm)
        Destination.CAESAR -> CaesarScreen(vm)
        Destination.COMPARE -> CompareScreen(vm)
        Destination.ACCOUNT -> AccountScreen(vm)
        Destination.ADMIN -> AdminScreen(vm)
    }
}
