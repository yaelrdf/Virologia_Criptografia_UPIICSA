package mx.ipn.upiicsa.prisma.ui

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ExitToApp
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.AccountCircle
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
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
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import mx.ipn.upiicsa.prisma.AppViewModel
import mx.ipn.upiicsa.prisma.ui.components.LocalWideLayout
import mx.ipn.upiicsa.prisma.ui.screens.AccountScreen
import mx.ipn.upiicsa.prisma.ui.screens.CaesarScreen
import mx.ipn.upiicsa.prisma.ui.screens.CompareScreen
import mx.ipn.upiicsa.prisma.ui.screens.HideScreen
import mx.ipn.upiicsa.prisma.ui.screens.LoginScreen
import mx.ipn.upiicsa.prisma.ui.screens.RevealScreen

enum class Destination(val label: String, val icon: ImageVector) {
    HIDE("Ocultar", Icons.Filled.Lock),
    REVEAL("Revelar", Icons.Filled.Search),
    CAESAR("César", Icons.Filled.Refresh),
    COMPARE("Comparador", Icons.AutoMirrored.Filled.List),
    ACCOUNT("Cuenta", Icons.Filled.AccountCircle),
}

@Composable
fun PrismaApp(vm: AppViewModel) {
    val snackbar = remember { SnackbarHostState() }
    LaunchedEffect(Unit) { vm.notices.collect { snackbar.showSnackbar(it) } }

    BoxWithConstraints(Modifier.fillMaxSize()) {
        val wide = maxWidth >= 600.dp
        CompositionLocalProvider(LocalWideLayout provides wide) {
            Scaffold(
                snackbarHost = { SnackbarHost(snackbar) },
            ) { padding ->
                Box(Modifier.padding(padding).fillMaxSize()) {
                    if (vm.session == null) LoginScreen(vm) else MainShell(vm, wide)
                }
            }
        }
    }
}

/** Navegación principal: NavigationRail en pantallas anchas, barra inferior en teléfono. */
@Composable
private fun MainShell(vm: AppViewModel, wide: Boolean) {
    var current by rememberSaveable { mutableStateOf(Destination.HIDE) }

    if (wide) {
        Row(Modifier.fillMaxSize()) {
            NavigationRail(
                windowInsets = WindowInsets(0),
                header = {
                    Text("PRISMA-L", style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.Bold,
                        color = MaterialTheme.colorScheme.primary, modifier = Modifier.padding(vertical = 12.dp))
                },
            ) {
                Destination.entries.forEach { d ->
                    NavigationRailItem(
                        selected = current == d,
                        onClick = { current = d },
                        icon = { Icon(d.icon, contentDescription = null) },
                        label = { Text(d.label) },
                    )
                }
                Box(Modifier.weight(1f))
                IconButton(onClick = vm::logout, modifier = Modifier.padding(bottom = 12.dp)) {
                    Icon(Icons.AutoMirrored.Filled.ExitToApp, contentDescription = "Cerrar sesión")
                }
            }
            Box(Modifier.weight(1f).fillMaxSize()) { DestinationContent(vm, current) }
        }
    } else {
        Scaffold(
            bottomBar = {
                NavigationBar(windowInsets = WindowInsets(0)) {
                    Destination.entries.forEach { d ->
                        NavigationBarItem(
                            selected = current == d,
                            onClick = { current = d },
                            icon = { Icon(d.icon, contentDescription = null) },
                            label = { Text(d.label, maxLines = 1) },
                        )
                    }
                }
            },
            contentWindowInsets = WindowInsets(0),
        ) { padding ->
            Box(Modifier.padding(padding).fillMaxSize()) { DestinationContent(vm, current) }
        }
    }
}

@Composable
private fun DestinationContent(vm: AppViewModel, destination: Destination) {
    when (destination) {
        Destination.HIDE -> HideScreen(vm)
        Destination.REVEAL -> RevealScreen(vm)
        Destination.CAESAR -> CaesarScreen(vm)
        Destination.COMPARE -> CompareScreen(vm)
        Destination.ACCOUNT -> AccountScreen(vm)
    }
}
