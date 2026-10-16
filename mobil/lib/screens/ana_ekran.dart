import 'dart:io';

import 'package:flutter/material.dart';
import 'package:image_picker/image_picker.dart';
import 'package:mobile_scanner/mobile_scanner.dart';
import 'package:qr_flutter/qr_flutter.dart';

import '../services/auth_service.dart';

typedef JsonMap = Map<String, dynamic>;

List<JsonMap> _asMaps(dynamic value) {
  if (value is! List) return const [];
  return value.whereType<Map>().map((item) {
    final result = Map<String, dynamic>.from(item);
    for (final entry in result.entries.toList()) {
      if (entry.key.isNotEmpty) {
        final key = '${entry.key[0].toUpperCase()}${entry.key.substring(1)}';
        result.putIfAbsent(key, () => entry.value);
      }
    }
    return result;
  }).toList();
}

int? _intValue(Object? value) =>
    value is num ? value.toInt() : int.tryParse('$value');

void _notify(BuildContext context, String message, {bool error = false}) {
  ScaffoldMessenger.of(context)
    ..hideCurrentSnackBar()
    ..showSnackBar(
      SnackBar(
        content: Text(message),
        backgroundColor: error ? Colors.red.shade700 : null,
      ),
    );
}

class AnaEkran extends StatefulWidget {
  const AnaEkran({
    required this.api,
    required this.user,
    required this.onLogout,
    super.key,
  });

  final AuthService api;
  final JsonMap user;
  final Future<void> Function() onLogout;

  @override
  State<AnaEkran> createState() => _AnaEkranState();
}

class _AnaEkranState extends State<AnaEkran> {
  int _selectedIndex = 0;
  bool _loggingOut = false;
  final Set<int> _visitedTabs = {0};

  bool get _isAdmin => widget.user['rol'] == 'Admin';

  Widget _pageAt(int index) => switch (index) {
    0 => _DashboardPage(
      api: widget.api,
      onOpenInventory: () => setState(() {
        _selectedIndex = 1;
        _visitedTabs.add(1);
      }),
    ),
    1 => _InventoryPage(api: widget.api, isAdmin: _isAdmin),
    2 => _AssignmentsPage(api: widget.api, isAdmin: _isAdmin),
    3 => _ProfilePage(api: widget.api, user: widget.user),
    4 when _isAdmin => _UsersPage(api: widget.api, currentUser: widget.user),
    _ => const SizedBox.shrink(),
  };

  Future<void> _logout() async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Oturumu kapat'),
        content: const Text('Hesabınızdan çıkış yapmak istiyor musunuz?'),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Vazgeç'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Çıkış yap'),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;

    setState(() => _loggingOut = true);
    try {
      await widget.onLogout();
    } catch (error) {
      if (mounted) _notify(context, error.toString(), error: true);
    } finally {
      if (mounted) setState(() => _loggingOut = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final labels = <String>[
      'Genel Bakış',
      'Envanter',
      'Zimmetler',
      'Profilim',
      if (_isAdmin) 'Kullanıcılar',
    ];
    final icons = <IconData>[
      Icons.space_dashboard_outlined,
      Icons.inventory_2_outlined,
      Icons.assignment_ind_outlined,
      Icons.account_circle_outlined,
      if (_isAdmin) Icons.manage_accounts_outlined,
    ];
    final pages = List<Widget>.generate(
      labels.length,
      (index) => _visitedTabs.contains(index)
          ? _pageAt(index)
          : const SizedBox.shrink(),
    );

    return Scaffold(
      appBar: AppBar(
        title: Row(
          children: [
            ClipOval(
              child: Image.asset(
                'assets/togu-logo-yuvarlak.png',
                width: 38,
                height: 38,
                fit: BoxFit.cover,
              ),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(
                    labels[_selectedIndex],
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                  Text(
                    '${widget.user['kullaniciAdi'] ?? ''} · ${_isAdmin ? 'Yönetici' : 'Personel'}',
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: Theme.of(context).textTheme.labelSmall,
                  ),
                ],
              ),
            ),
          ],
        ),
        actions: [
          IconButton(
            tooltip: 'QR kod okut',
            onPressed: () => Navigator.of(context).push(
              MaterialPageRoute<void>(
                builder: (_) => _QrScanPage(api: widget.api, isAdmin: _isAdmin),
              ),
            ),
            icon: const Icon(Icons.qr_code_scanner),
          ),
          IconButton(
            tooltip: 'Oturumu kapat',
            onPressed: _loggingOut ? null : _logout,
            icon: _loggingOut
                ? const SizedBox.square(
                    dimension: 18,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.logout),
          ),
        ],
      ),
      body: IndexedStack(index: _selectedIndex, children: pages),
      bottomNavigationBar: NavigationBar(
        selectedIndex: _selectedIndex,
        onDestinationSelected: (index) => setState(() {
          _selectedIndex = index;
          _visitedTabs.add(index);
        }),
        destinations: [
          for (var index = 0; index < labels.length; index++)
            NavigationDestination(
              icon: Icon(icons[index]),
              label: labels[index],
            ),
        ],
      ),
    );
  }
}

class _DashboardPage extends StatefulWidget {
  const _DashboardPage({required this.api, required this.onOpenInventory});

  final AuthService api;
  final VoidCallback onOpenInventory;

  @override
  State<_DashboardPage> createState() => _DashboardPageState();
}

class _DashboardPageState extends State<_DashboardPage> {
  JsonMap? _stats;
  Object? _error;
  bool _loading = true;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final result = await widget.api.request('GET', 'Demirbas/istatistikler');
      if (mounted) {
        setState(() => _stats = Map<String, dynamic>.from(result as Map));
      }
    } catch (error) {
      if (mounted) setState(() => _error = error);
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    if (_loading && _stats == null) {
      return const Center(child: CircularProgressIndicator());
    }
    if (_error != null && _stats == null) {
      return _LoadError(error: _error!, onRetry: _load);
    }
    final stats = _stats ?? const <String, dynamic>{};
    return RefreshIndicator(
      onRefresh: _load,
      child: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          Text(
            'Demirbaş durum özeti',
            style: Theme.of(context).textTheme.titleLarge
                ?.copyWith(fontWeight: FontWeight.bold),
          ),
          const SizedBox(height: 4),
          const Text('Güncel envanter ve arıza görünümü'),
          const SizedBox(height: 18),
          _StatCard(
            title: 'Toplam cihaz',
            count: _intValue(stats['toplam']) ?? 0,
            icon: Icons.devices_other,
            color: const Color(0xFF1746A2),
          ),
          const SizedBox(height: 12),
          _StatCard(
            title: 'Boşta bekleyen',
            count: _intValue(stats['bostaBekleyen']) ?? 0,
            icon: Icons.inventory_outlined,
            color: const Color(0xFF16835D),
          ),
          const SizedBox(height: 12),
          _StatCard(
            title: 'Arızalı cihaz',
            count: _intValue(stats['arizali']) ?? 0,
            icon: Icons.build_circle_outlined,
            color: const Color(0xFFCE4A43),
          ),
          const SizedBox(height: 20),
          FilledButton.tonalIcon(
            onPressed: widget.onOpenInventory,
            icon: const Icon(Icons.arrow_forward),
            label: const Text('Envanteri görüntüle'),
          ),
          if (_error != null) ...[
            const SizedBox(height: 12),
            Text(
              'Yenileme başarısız: $_error',
              style: TextStyle(color: Theme.of(context).colorScheme.error),
            ),
          ],
        ],
      ),
    );
  }
}

class _StatCard extends StatelessWidget {
  const _StatCard({
    required this.title,
    required this.count,
    required this.icon,
    required this.color,
  });

  final String title;
  final int count;
  final IconData icon;
  final Color color;

  @override
  Widget build(BuildContext context) {
    return Card(
      elevation: 0,
      color: Colors.white,
      child: Padding(
        padding: const EdgeInsets.all(18),
        child: Row(
          children: [
            CircleAvatar(
              radius: 25,
              backgroundColor: color.withValues(alpha: 0.12),
              foregroundColor: color,
              child: Icon(icon),
            ),
            const SizedBox(width: 16),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(title, style: Theme.of(context).textTheme.bodyMedium),
                  const SizedBox(height: 4),
                  Text(
                    '$count',
                    style: Theme.of(context).textTheme.headlineMedium
                        ?.copyWith(fontWeight: FontWeight.bold, color: color),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _InventoryPage extends StatefulWidget {
  const _InventoryPage({required this.api, required this.isAdmin});

  final AuthService api;
  final bool isAdmin;

  @override
  State<_InventoryPage> createState() => _InventoryPageState();
}

class _InventoryPageState extends State<_InventoryPage> {
  final _searchController = TextEditingController();
  List<JsonMap> _assets = [];
  List<JsonMap> _departments = [];
  String _status = 'Tumu';
  int? _departmentId;
  int _page = 1;
  final int _pageSize = 10;
  Object? _error;
  bool _loading = true;

  static const _statuses = [
    'Tumu',
    'Kullanilabilir',
    'BostaBekleyen',
    'Zimmetli',
    'Arizali',
    'Bakimda',
  ];

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final results = await Future.wait([
        widget.api.request('GET', 'Demirbas'),
        widget.api.request('GET', 'Demirbas/filtreler'),
      ]);
      final filters = Map<String, dynamic>.from(results[1] as Map);
      if (mounted) {
        setState(() {
          _assets = _asMaps(results[0]);
          _departments = _asMaps(filters['departmanlar']);
          _page = 1;
        });
      }
    } catch (error) {
      if (mounted) setState(() => _error = error);
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  List<JsonMap> get _filtered {
    final search = _searchController.text.trim().toLowerCase();
    return _assets.where((asset) {
      final statusMatches =
          _status == 'Tumu' || asset['Durum']?.toString() == _status;
      final departmentMatches =
          _departmentId == null ||
          _intValue(asset['DepartmanId']) == _departmentId;
      final searchMatches =
          search.isEmpty ||
          [
            asset['DemirbasAdi'],
            asset['Kategori'],
            asset['Marka'],
            asset['Model'],
            asset['SeriNumarasi'],
            asset['QrKodu'],
            asset['PersonelAdi'],
          ].whereType<Object>().any(
            (value) => value.toString().toLowerCase().contains(search),
          );
      return statusMatches && departmentMatches && searchMatches;
    }).toList();
  }

  Future<void> _editAsset([JsonMap? asset]) async {
    final form = await showDialog<JsonMap>(
      context: context,
      builder: (context) => _AssetForm(asset: asset),
    );
    if (form == null) return;
    try {
      final id = _intValue(asset?['DemirbasId']);
      await widget.api.request(
        id == null ? 'POST' : 'PUT',
        id == null ? 'Demirbas' : 'Demirbas/$id',
        body: form,
      );
      if (!mounted) return;
      _notify(
        context,
        id == null ? 'Demirbaş eklendi.' : 'Demirbaş güncellendi.',
      );
      await _load();
    } catch (error) {
      if (mounted) _notify(context, error.toString(), error: true);
    }
  }

  Future<void> _deleteAsset(JsonMap asset) async {
    final id = _intValue(asset['DemirbasId']);
    if (id == null) return;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Demirbaşı sil'),
        content: Text('"${asset['DemirbasAdi']}" silinsin mi?'),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Vazgeç'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Sil'),
          ),
        ],
      ),
    );
    if (confirmed != true) return;
    try {
      await widget.api.request('DELETE', 'Demirbas/$id');
      if (!mounted) return;
      _notify(context, 'Demirbaş silindi.');
      await _load();
    } catch (error) {
      if (mounted) _notify(context, error.toString(), error: true);
    }
  }

  void _showDetails(JsonMap asset) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      builder: (context) => SafeArea(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(20, 4, 20, 24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                asset['DemirbasAdi']?.toString() ?? 'Demirbaş',
                style: Theme.of(context).textTheme.titleLarge,
              ),
              const SizedBox(height: 12),
              _DetailRow('Kategori', asset['Kategori']),
              _DetailRow(
                'Marka / Model',
                '${asset['Marka'] ?? '-'} / ${asset['Model'] ?? '-'}',
              ),
              _DetailRow('Seri numarası', asset['SeriNumarasi']),
              _DetailRow('Durum', asset['Durum']),
              _DetailRow('Zimmetli personel', asset['PersonelAdi']),
              _DetailRow('Departman', asset['DepartmanAdi']),
              _DetailRow(
                'Alım tarihi',
                asset['SatinAlinmaTarihi']?.toString().split('T').first,
              ),
              _DetailRow(
                'Garanti bitişi',
                asset['GarantiBitisTarihi']?.toString().split('T').first,
              ),
              _DetailRow('QR / barkod', asset['QrKodu'] ?? asset['BarkodKodu']),
              if ((asset['QrKodu'] ?? asset['BarkodKodu']) != null)
                Center(
                  child: QrImageView(
                    data: (asset['QrKodu'] ?? asset['BarkodKodu']).toString(),
                    size: 150,
                    backgroundColor: Colors.white,
                  ),
                ),
              if (widget.isAdmin) ...[
                const SizedBox(height: 12),
                Wrap(
                  spacing: 8,
                  children: [
                    OutlinedButton.icon(
                      onPressed: () {
                        Navigator.pop(context);
                        _editAsset(asset);
                      },
                      icon: const Icon(Icons.edit_outlined),
                      label: const Text('Düzenle'),
                    ),
                    TextButton.icon(
                      onPressed: () {
                        Navigator.pop(context);
                        _deleteAsset(asset);
                      },
                      icon: const Icon(Icons.delete_outline),
                      label: const Text('Sil'),
                    ),
                  ],
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    if (_loading && _assets.isEmpty) {
      return const Center(child: CircularProgressIndicator());
    }
    if (_error != null && _assets.isEmpty) {
      return _LoadError(error: _error!, onRetry: _load);
    }

    final filtered = _filtered;
    final pageCount = (filtered.length / _pageSize).ceil().clamp(1, 999999);
    if (_page > pageCount) {
      _page = pageCount;
    }
    final start = (_page - 1) * _pageSize;
    final visible = filtered.skip(start).take(_pageSize).toList();

    return Stack(
      children: [
        RefreshIndicator(
          onRefresh: _load,
          child: ListView(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 88),
            children: [
              TextField(
                controller: _searchController,
                onChanged: (_) => setState(() => _page = 1),
                decoration: InputDecoration(
                  hintText: 'Ad, kategori, marka, seri no ara',
                  prefixIcon: const Icon(Icons.search),
                  suffixIcon: _searchController.text.isEmpty
                      ? null
                      : IconButton(
                          onPressed: () => setState(() {
                            _searchController.clear();
                            _page = 1;
                          }),
                          icon: const Icon(Icons.close),
                        ),
                ),
              ),
              const SizedBox(height: 10),
              DropdownButtonFormField<String>(
                initialValue: _statuses.contains(_status) ? _status : 'Tumu',
                decoration: const InputDecoration(labelText: 'Durum'),
                items: _statuses
                    .map(
                      (status) => DropdownMenuItem(
                        value: status,
                        child: Text(_statusLabel(status)),
                      ),
                    )
                    .toList(),
                onChanged: (value) => setState(() {
                  _status = value ?? 'Tumu';
                  _page = 1;
                }),
              ),
              const SizedBox(height: 10),
              DropdownButtonFormField<int?>(
                initialValue: _departmentId,
                decoration: const InputDecoration(labelText: 'Departman'),
                items: [
                  const DropdownMenuItem<int?>(
                    value: null,
                    child: Text('Tüm departmanlar'),
                  ),
                  ..._departments.map(
                    (department) => DropdownMenuItem<int?>(
                      value: _intValue(department['departmanId']),
                      child: Text(department['ad']?.toString() ?? ''),
                    ),
                  ),
                ],
                onChanged: (value) => setState(() {
                  _departmentId = value;
                  _page = 1;
                }),
              ),
              const SizedBox(height: 12),
              Text('${filtered.length} demirbaş'),
              if (visible.isEmpty)
                const Padding(
                  padding: EdgeInsets.symmetric(vertical: 40),
                  child: Center(child: Text('Aramanızla eşleşen kayıt yok.')),
                ),
              ...visible.map(
                (asset) => _AssetCard(
                  asset: asset,
                  onTap: () => _showDetails(asset),
                  onEdit: widget.isAdmin ? () => _editAsset(asset) : null,
                  onDelete: widget.isAdmin ? () => _deleteAsset(asset) : null,
                ),
              ),
              if (filtered.isNotEmpty)
                Row(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    IconButton(
                      tooltip: 'Önceki sayfa',
                      onPressed: _page > 1
                          ? () => setState(() => _page--)
                          : null,
                      icon: const Icon(Icons.chevron_left),
                    ),
                    Text('Sayfa $_page / $pageCount'),
                    IconButton(
                      tooltip: 'Sonraki sayfa',
                      onPressed: _page < pageCount
                          ? () => setState(() => _page++)
                          : null,
                      icon: const Icon(Icons.chevron_right),
                    ),
                  ],
                ),
              if (_error != null)
                Text(
                  'Yenileme başarısız: $_error',
                  style: TextStyle(color: Theme.of(context).colorScheme.error),
                ),
            ],
          ),
        ),
        if (widget.isAdmin)
          Positioned(
            right: 20,
            bottom: 16,
            child: FloatingActionButton.extended(
              onPressed: () => _editAsset(),
              icon: const Icon(Icons.add),
              label: const Text('Demirbaş ekle'),
            ),
          ),
      ],
    );
  }
}

class _AssetCard extends StatelessWidget {
  const _AssetCard({
    required this.asset,
    required this.onTap,
    this.onEdit,
    this.onDelete,
  });

  final JsonMap asset;
  final VoidCallback onTap;
  final VoidCallback? onEdit;
  final VoidCallback? onDelete;

  @override
  Widget build(BuildContext context) {
    final status = asset['Durum']?.toString() ?? 'Bilinmiyor';
    return Card(
      elevation: 0,
      color: Colors.white,
      margin: const EdgeInsets.only(top: 8),
      child: ListTile(
        onTap: onTap,
        contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 6),
        leading: CircleAvatar(
          backgroundColor: const Color(0xFF1746A2).withValues(alpha: 0.1),
          child: const Icon(Icons.devices_outlined, color: Color(0xFF1746A2)),
        ),
        title: Text(
          asset['DemirbasAdi']?.toString() ?? 'Demirbaş',
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
        subtitle: Padding(
          padding: const EdgeInsets.only(top: 5),
          child: Text(
            '${asset['Kategori'] ?? '-'} · ${asset['Marka'] ?? ''} ${asset['Model'] ?? ''}\n'
            '${asset['PersonelAdi'] ?? asset['DepartmanAdi'] ?? 'Atanmamış'}',
            maxLines: 2,
            overflow: TextOverflow.ellipsis,
          ),
        ),
        isThreeLine: true,
        trailing: onEdit == null && onDelete == null
            ? _StatusChip(label: _statusLabel(status))
            : PopupMenuButton<String>(
                onSelected: (action) =>
                    action == 'edit' ? onEdit?.call() : onDelete?.call(),
                itemBuilder: (context) => const [
                  PopupMenuItem(value: 'edit', child: Text('Düzenle')),
                  PopupMenuItem(value: 'delete', child: Text('Sil')),
                ],
              ),
      ),
    );
  }
}

class _AssetForm extends StatefulWidget {
  const _AssetForm({this.asset});

  final JsonMap? asset;

  @override
  State<_AssetForm> createState() => _AssetFormState();
}

class _AssetFormState extends State<_AssetForm> {
  final _formKey = GlobalKey<FormState>();
  late final TextEditingController _name;
  late final TextEditingController _category;
  late final TextEditingController _brand;
  late final TextEditingController _model;
  late final TextEditingController _serial;
  late final TextEditingController _date;
  late final TextEditingController _warrantyDate;
  late String _status;

  static const _statuses = [
    'Kullanilabilir',
    'BostaBekleyen',
    'Zimmetli',
    'Arizali',
    'Bakimda',
  ];

  @override
  void initState() {
    super.initState();
    final asset = widget.asset ?? const <String, dynamic>{};
    _name = TextEditingController(text: asset['DemirbasAdi']?.toString());
    _category = TextEditingController(text: asset['Kategori']?.toString());
    _brand = TextEditingController(text: asset['Marka']?.toString());
    _model = TextEditingController(text: asset['Model']?.toString());
    _serial = TextEditingController(text: asset['SeriNumarasi']?.toString());
    _date = TextEditingController(
      text: asset['SatinAlinmaTarihi']?.toString().split('T').first,
    );
    _warrantyDate = TextEditingController(
      text: asset['GarantiBitisTarihi']?.toString().split('T').first,
    );
    final currentStatus = asset['Durum']?.toString();
    _status = _statuses.contains(currentStatus) ? currentStatus! : _statuses[0];
  }

  @override
  void dispose() {
    _name.dispose();
    _category.dispose();
    _brand.dispose();
    _model.dispose();
    _serial.dispose();
    _date.dispose();
    _warrantyDate.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: Text(widget.asset == null ? 'Demirbaş ekle' : 'Demirbaşı düzenle'),
      content: SizedBox(
        width: 440,
        child: Form(
          key: _formKey,
          child: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                _formField(_name, 'Demirbaş adı', required: true),
                _formField(_category, 'Kategori', required: true),
                _formField(_brand, 'Marka'),
                _formField(_model, 'Model'),
                _formField(_serial, 'Seri numarası'),
                _formField(_date, 'Alım tarihi (YYYY-AA-GG)'),
                _formField(_warrantyDate, 'Garanti bitiş tarihi (YYYY-AA-GG)'),
                DropdownButtonFormField<String>(
                  initialValue: _status,
                  decoration: const InputDecoration(labelText: 'Durum'),
                  items: _statuses
                      .map(
                        (status) => DropdownMenuItem(
                          value: status,
                          child: Text(_statusLabel(status)),
                        ),
                      )
                      .toList(),
                  onChanged: (value) =>
                      setState(() => _status = value ?? _statuses[0]),
                ),
              ],
            ),
          ),
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.pop(context),
          child: const Text('İptal'),
        ),
        FilledButton(
          onPressed: () {
            if (!_formKey.currentState!.validate()) return;
            Navigator.pop(context, <String, dynamic>{
              'demirbasAdi': _name.text.trim(),
              'kategori': _category.text.trim(),
              'marka': _brand.text.trim(),
              'model': _model.text.trim(),
              'seriNumarasi': _serial.text.trim(),
              'satinAlinmaTarihi': _date.text.trim(),
              'garantiBitisTarihi': _warrantyDate.text.trim(),
              'durum': _status,
            });
          },
          child: const Text('Kaydet'),
        ),
      ],
    );
  }

  Widget _formField(
    TextEditingController controller,
    String label, {
    bool required = false,
  }) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 10),
      child: TextFormField(
        controller: controller,
        decoration: InputDecoration(labelText: label),
        validator: required
            ? (value) => value == null || value.trim().isEmpty
                  ? '$label zorunludur.'
                  : null
            : null,
      ),
    );
  }
}

class _DetailRow extends StatelessWidget {
  const _DetailRow(this.label, this.value);

  final String label;
  final Object? value;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 5),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 132,
            child: Text(label, style: const TextStyle(color: Colors.black54)),
          ),
          Expanded(
            child: Text(
              value?.toString().isNotEmpty == true ? value.toString() : '-',
            ),
          ),
        ],
      ),
    );
  }
}

class _StatusChip extends StatelessWidget {
  const _StatusChip({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) => Chip(
    label: Text(label, style: const TextStyle(fontSize: 11)),
    visualDensity: VisualDensity.compact,
    padding: EdgeInsets.zero,
  );
}

String _statusLabel(String status) => switch (status) {
  'Tumu' => 'Tümü',
  'Kullanilabilir' => 'Kullanılabilir',
  'BostaBekleyen' => 'Boşta bekleyen',
  'Zimmetli' => 'Zimmetli',
  'Arizali' => 'Arızalı',
  'Bakimda' => 'Bakımda',
  'Aktif' => 'Aktif',
  'Eski' => 'Eski',
  'İade Edildi' => 'İade edildi',
  _ => status,
};

class _LoadError extends StatelessWidget {
  const _LoadError({required this.error, required this.onRetry});

  final Object error;
  final Future<void> Function() onRetry;

  @override
  Widget build(BuildContext context) => Center(
    child: Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          const Icon(Icons.error_outline, size: 42),
          const SizedBox(height: 12),
          Text(error.toString(), textAlign: TextAlign.center),
          const SizedBox(height: 12),
          OutlinedButton.icon(
            onPressed: onRetry,
            icon: const Icon(Icons.refresh),
            label: const Text('Yeniden dene'),
          ),
        ],
      ),
    ),
  );
}

class _AssignmentsPage extends StatefulWidget {
  const _AssignmentsPage({required this.api, required this.isAdmin});

  final AuthService api;
  final bool isAdmin;

  @override
  State<_AssignmentsPage> createState() => _AssignmentsPageState();
}

class _AssignmentsPageState extends State<_AssignmentsPage> {
  final _searchController = TextEditingController();
  List<JsonMap> _assignments = [];
  List<JsonMap> _assets = [];
  List<JsonMap> _personnel = [];
  List<JsonMap> _departments = [];
  String _status = 'Tumu';
  int? _personnelId;
  int? _departmentId;
  Object? _error;
  bool _loading = true;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final results = await Future.wait([
        widget.api.request('GET', 'Demirbas/zimmetler'),
        widget.api.request('GET', 'Demirbas'),
        widget.api.request('GET', 'Demirbas/filtreler'),
      ]);
      final filters = Map<String, dynamic>.from(results[2] as Map);
      if (mounted) {
        setState(() {
          _assignments = _asMaps(results[0]);
          _assets = _asMaps(results[1]);
          _personnel = _asMaps(filters['personeller']);
          _departments = _asMaps(filters['departmanlar']);
        });
      }
    } catch (error) {
      if (mounted) setState(() => _error = error);
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  List<JsonMap> get _filtered {
    final search = _searchController.text.trim().toLowerCase();
    return _assignments.where((assignment) {
      final status = assignment['durum']?.toString() ?? '';
      final isActive = status == 'Aktif';
      final statusMatches = switch (_status) {
        'Aktif' => isActive,
        'Gecmis' => !isActive,
        _ => true,
      };
      final personMatches =
          _personnelId == null ||
          _intValue(assignment['personelId']) == _personnelId;
      final departmentMatches =
          _departmentId == null ||
          _intValue(assignment['departmanId']) == _departmentId;
      final searchMatches =
          search.isEmpty ||
          [
            assignment['demirbasAdi'],
            assignment['personelAdi'],
            assignment['departmanAdi'],
            assignment['aciklama'],
          ].whereType<Object>().any(
            (value) => value.toString().toLowerCase().contains(search),
          );
      return statusMatches &&
          personMatches &&
          departmentMatches &&
          searchMatches;
    }).toList();
  }

  Future<void> _assignAsset() async {
    final available = _assets
        .where((asset) => asset['Durum']?.toString() != 'Zimmetli')
        .toList();
    if (available.isEmpty) {
      _notify(
        context,
        'Zimmetlenebilecek boşta cihaz bulunmuyor.',
        error: true,
      );
      return;
    }
    if (_personnel.isEmpty) {
      _notify(
        context,
        'Zimmetleme için önce bir personel kaydı bulunmalıdır.',
        error: true,
      );
      return;
    }

    final form = await showDialog<JsonMap>(
      context: context,
      builder: (context) => _AssignmentForm(
        assets: available,
        personnel: _personnel,
        departments: _departments,
      ),
    );
    if (form == null) return;
    try {
      await widget.api.request('POST', 'Demirbas/zimmetle', body: form);
      if (!mounted) return;
      _notify(context, 'Demirbaş personele zimmetlendi.');
      await _load();
    } catch (error) {
      if (mounted) _notify(context, error.toString(), error: true);
    }
  }

  Future<void> _returnAsset(JsonMap assignment) async {
    final id = _intValue(assignment['demirbasId']);
    if (id == null) return;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Zimmetten düş'),
        content: Text(
          '"${assignment['demirbasAdi'] ?? 'Demirbaş'}" zimmetten düşürülsün mü?',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Vazgeç'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Zimmetten düş'),
          ),
        ],
      ),
    );
    if (confirmed != true) return;
    try {
      await widget.api.request(
        'POST',
        'Demirbas/zimmetten-dusun/$id',
        body: {},
      );
      if (!mounted) return;
      _notify(context, 'Cihaz zimmetten düşürüldü.');
      await _load();
    } catch (error) {
      if (mounted) _notify(context, error.toString(), error: true);
    }
  }

  @override
  Widget build(BuildContext context) {
    if (_loading && _assignments.isEmpty) {
      return const Center(child: CircularProgressIndicator());
    }
    if (_error != null && _assignments.isEmpty) {
      return _LoadError(error: _error!, onRetry: _load);
    }
    final visible = _filtered;
    return Stack(
      children: [
        RefreshIndicator(
          onRefresh: _load,
          child: ListView(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 88),
            children: [
              TextField(
                controller: _searchController,
                onChanged: (_) => setState(() {}),
                decoration: const InputDecoration(
                  hintText: 'Cihaz, personel veya departman ara',
                  prefixIcon: Icon(Icons.search),
                ),
              ),
              const SizedBox(height: 10),
              DropdownButtonFormField<String>(
                initialValue: _status,
                decoration: const InputDecoration(labelText: 'Zimmet durumu'),
                items: const [
                  DropdownMenuItem(value: 'Tumu', child: Text('Tüm kayıtlar')),
                  DropdownMenuItem(value: 'Aktif', child: Text('Aktif')),
                  DropdownMenuItem(
                    value: 'Gecmis',
                    child: Text('Geçmiş / iade'),
                  ),
                ],
                onChanged: (value) => setState(() => _status = value ?? 'Tumu'),
              ),
              const SizedBox(height: 10),
              DropdownButtonFormField<int?>(
                initialValue: _personnelId,
                decoration: const InputDecoration(labelText: 'Personel'),
                items: [
                  const DropdownMenuItem<int?>(
                    value: null,
                    child: Text('Tüm personeller'),
                  ),
                  ..._personnel.map(
                    (item) => DropdownMenuItem<int?>(
                      value: _intValue(item['personelId']),
                      child: Text(item['adSoyad']?.toString() ?? ''),
                    ),
                  ),
                ],
                onChanged: (value) => setState(() => _personnelId = value),
              ),
              const SizedBox(height: 10),
              DropdownButtonFormField<int?>(
                initialValue: _departmentId,
                decoration: const InputDecoration(labelText: 'Departman'),
                items: [
                  const DropdownMenuItem<int?>(
                    value: null,
                    child: Text('Tüm departmanlar'),
                  ),
                  ..._departments.map(
                    (item) => DropdownMenuItem<int?>(
                      value: _intValue(item['departmanId']),
                      child: Text(item['ad']?.toString() ?? ''),
                    ),
                  ),
                ],
                onChanged: (value) => setState(() => _departmentId = value),
              ),
              const SizedBox(height: 12),
              Text('${visible.length} zimmet kaydı'),
              if (visible.isEmpty)
                const Padding(
                  padding: EdgeInsets.symmetric(vertical: 40),
                  child: Center(child: Text('Gösterilecek zimmet kaydı yok.')),
                ),
              ...visible.map((assignment) {
                final active = assignment['durum'] == 'Aktif';
                return Card(
                  elevation: 0,
                  color: Colors.white,
                  margin: const EdgeInsets.only(top: 8),
                  child: ListTile(
                    leading: CircleAvatar(
                      backgroundColor: const Color(0xFF1746A2)
                          .withValues(alpha: 0.1),
                      child: const Icon(Icons.assignment_ind_outlined),
                    ),
                    title: Text(
                      assignment['demirbasAdi']?.toString() ?? 'Demirbaş',
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                    subtitle: Padding(
                      padding: const EdgeInsets.only(top: 5),
                      child: Text(
                        '${assignment['personelAdi'] ?? 'Personel yok'} · '
                        '${assignment['departmanAdi'] ?? 'Departman yok'}\n'
                        '${assignment['zimmetTarihi'] ?? ''}',
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                    isThreeLine: true,
                    trailing: widget.isAdmin && active
                        ? IconButton(
                            tooltip: 'Zimmetten düş',
                            onPressed: () => _returnAsset(assignment),
                            icon: const Icon(Icons.assignment_return_outlined),
                          )
                        : _StatusChip(
                            label: _statusLabel(
                              assignment['durum']?.toString() ?? 'Bilinmiyor',
                            ),
                          ),
                  ),
                );
              }),
              if (_error != null)
                Text(
                  'Yenileme başarısız: $_error',
                  style: TextStyle(color: Theme.of(context).colorScheme.error),
                ),
            ],
          ),
        ),
        if (widget.isAdmin)
          Positioned(
            right: 20,
            bottom: 16,
            child: FloatingActionButton.extended(
              onPressed: _assignAsset,
              icon: const Icon(Icons.assignment_add),
              label: const Text('Zimmetle'),
            ),
          ),
      ],
    );
  }
}

class _AssignmentForm extends StatefulWidget {
  const _AssignmentForm({
    required this.assets,
    required this.personnel,
    required this.departments,
  });

  final List<JsonMap> assets;
  final List<JsonMap> personnel;
  final List<JsonMap> departments;

  @override
  State<_AssignmentForm> createState() => _AssignmentFormState();
}

class _AssignmentFormState extends State<_AssignmentForm> {
  final _formKey = GlobalKey<FormState>();
  final _descriptionController = TextEditingController();
  int? _assetId;
  int? _personnelId;
  int? _departmentId;

  @override
  void dispose() {
    _descriptionController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('Demirbaş zimmetle'),
      scrollable: true,
      content: SizedBox(
        width: 440,
        child: Form(
          key: _formKey,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              DropdownButtonFormField<int>(
                initialValue: _assetId,
                isExpanded: true,
                decoration: const InputDecoration(labelText: 'Demirbaş'),
                items: widget.assets
                    .map(
                      (asset) => DropdownMenuItem<int>(
                        value: _intValue(asset['DemirbasId']),
                        child: Text(
                          asset['DemirbasAdi']?.toString() ?? '',
                          overflow: TextOverflow.ellipsis,
                        ),
                      ),
                    )
                    .toList(),
                validator: (value) => value == null ? 'Cihaz seçin.' : null,
                onChanged: (value) => setState(() => _assetId = value),
              ),
              const SizedBox(height: 10),
              DropdownButtonFormField<int>(
                initialValue: _personnelId,
                isExpanded: true,
                decoration: const InputDecoration(labelText: 'Personel'),
                items: widget.personnel
                    .map(
                      (person) => DropdownMenuItem<int>(
                        value: _intValue(person['personelId']),
                        child: Text(person['adSoyad']?.toString() ?? ''),
                      ),
                    )
                    .toList(),
                validator: (value) => value == null ? 'Personel seçin.' : null,
                onChanged: (value) => setState(() => _personnelId = value),
              ),
              const SizedBox(height: 10),
              Align(
                alignment: Alignment.centerLeft,
                child: Text(
                  'Departman',
                  style: Theme.of(context).textTheme.labelLarge,
                ),
              ),
              const SizedBox(height: 4),
              DropdownButtonFormField<int?>(
                initialValue: _departmentId,
                isExpanded: true,
                decoration: const InputDecoration(hintText: 'Departman seçin'),
                items: [
                  const DropdownMenuItem<int?>(
                    value: null,
                    child: Text(
                      'Personelin departmanını kullan',
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                  ...widget.departments.map(
                    (department) => DropdownMenuItem<int?>(
                      value: _intValue(department['departmanId']),
                      child: Text(
                        department['ad']?.toString() ?? '',
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                  ),
                ],
                onChanged: (value) => setState(() => _departmentId = value),
              ),
              const SizedBox(height: 10),
              TextFormField(
                controller: _descriptionController,
                decoration: const InputDecoration(labelText: 'Açıklama'),
                maxLines: 2,
                maxLength: 500,
              ),
            ],
          ),
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.pop(context),
          child: const Text('İptal'),
        ),
        FilledButton(
          onPressed: () {
            if (!_formKey.currentState!.validate()) return;
            final selectedPerson = widget.personnel.firstWhere(
              (person) => _intValue(person['personelId']) == _personnelId,
              orElse: () => const <String, dynamic>{},
            );
            Navigator.pop(context, <String, dynamic>{
              'demirbasId': _assetId,
              'personelId': _personnelId,
              'departmanId':
                  _departmentId ??
                  _intValue(selectedPerson['departmanId']) ??
                  0,
              'aciklama': _descriptionController.text.trim(),
            });
          },
          child: const Text('Zimmetle'),
        ),
      ],
    );
  }
}

class _UsersPage extends StatefulWidget {
  const _UsersPage({required this.api, required this.currentUser});

  final AuthService api;
  final JsonMap currentUser;

  @override
  State<_UsersPage> createState() => _UsersPageState();
}

class _UsersPageState extends State<_UsersPage> {
  final _searchController = TextEditingController();
  List<JsonMap> _users = [];
  List<JsonMap> _personnel = [];
  Object? _error;
  bool _loading = true;
  int _page = 1;
  static const _pageSize = 10;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final filters = Map<String, dynamic>.from(
        await widget.api.request('GET', 'Demirbas/filtreler') as Map,
      );
      final result = await widget.api.request('GET', 'auth/kullanicilar');
      if (mounted) {
        setState(() {
          _users = _asMaps(result);
          _personnel = _asMaps(filters['personeller']);
          _page = 1;
        });
      }
    } catch (error) {
      if (mounted) setState(() => _error = error);
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _editUser([JsonMap? user]) async {
    final form = await showDialog<JsonMap>(
      context: context,
      builder: (context) => _UserForm(user: user, personnel: _personnel),
    );
    if (form == null) return;
    try {
      final id = _intValue(user?['kullaniciId']);
      await widget.api.request(
        id == null ? 'POST' : 'PUT',
        id == null ? 'auth/kullanicilar' : 'auth/kullanicilar/$id',
        body: form,
      );
      if (!mounted) return;
      _notify(
        context,
        id == null ? 'Kullanıcı oluşturuldu.' : 'Kullanıcı güncellendi.',
      );
      await _load();
    } catch (error) {
      if (mounted) _notify(context, error.toString(), error: true);
    }
  }

  Future<void> _deleteUser(JsonMap user) async {
    final id = _intValue(user['kullaniciId']);
    if (id == null) return;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Kullanıcıyı sil'),
        content: Text('"${user['kullaniciAdi']}" hesabı silinsin mi?'),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Vazgeç'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Sil'),
          ),
        ],
      ),
    );
    if (confirmed != true) return;
    try {
      await widget.api.request('DELETE', 'auth/kullanicilar/$id');
      if (!mounted) return;
      _notify(context, 'Kullanıcı silindi.');
      await _load();
    } catch (error) {
      if (mounted) _notify(context, error.toString(), error: true);
    }
  }

  @override
  Widget build(BuildContext context) {
    if (_loading && _users.isEmpty) {
      return const Center(child: CircularProgressIndicator());
    }
    if (_error != null && _users.isEmpty) {
      return _LoadError(error: _error!, onRetry: _load);
    }
    final search = _searchController.text.trim().toLowerCase();
    final filtered = _users
        .where(
          (user) =>
              user['kullaniciAdi']?.toString().toLowerCase().contains(search) ??
              false,
        )
        .toList();
    final pageCount = (filtered.length / _pageSize).ceil().clamp(1, 999999);
    if (_page > pageCount) {
      _page = pageCount;
    }
    final visible = filtered
        .skip((_page - 1) * _pageSize)
        .take(_pageSize)
        .toList();
    final currentName = widget.currentUser['kullaniciAdi']?.toString();

    return Stack(
      children: [
        RefreshIndicator(
          onRefresh: _load,
          child: ListView(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 88),
            children: [
              TextField(
                controller: _searchController,
                onChanged: (_) => setState(() => _page = 1),
                decoration: const InputDecoration(
                  hintText: 'Kullanıcı adı ara',
                  prefixIcon: Icon(Icons.search),
                ),
              ),
              const SizedBox(height: 12),
              Text('${filtered.length} kullanıcı'),
              if (visible.isEmpty)
                const Padding(
                  padding: EdgeInsets.symmetric(vertical: 40),
                  child: Center(child: Text('Kullanıcı bulunamadı.')),
                ),
              ...visible.map((user) {
                final username = user['kullaniciAdi']?.toString() ?? '';
                return Card(
                  elevation: 0,
                  color: Colors.white,
                  margin: const EdgeInsets.only(top: 8),
                  child: ListTile(
                    leading: CircleAvatar(
                      child: Icon(
                        user['rol'] == 'Admin'
                            ? Icons.admin_panel_settings_outlined
                            : Icons.person_outline,
                      ),
                    ),
                    title: Text(username),
                    subtitle: Text(
                      '${user['rol'] == 'Admin' ? 'Yönetici' : 'Personel'}'
                      '${username == currentName ? ' · Siz' : ''}'
                      '${_personnelName(user['personelId'])}',
                    ),
                    trailing: PopupMenuButton<String>(
                      onSelected: (action) => action == 'edit'
                          ? _editUser(user)
                          : _deleteUser(user),
                      itemBuilder: (context) => [
                        const PopupMenuItem(
                          value: 'edit',
                          child: Text('Düzenle / parola değiştir'),
                        ),
                        if (username != currentName)
                          const PopupMenuItem(
                            value: 'delete',
                            child: Text('Sil'),
                          ),
                      ],
                    ),
                  ),
                );
              }),
              if (filtered.isNotEmpty)
                Row(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    IconButton(
                      onPressed: _page > 1
                          ? () => setState(() => _page--)
                          : null,
                      icon: const Icon(Icons.chevron_left),
                    ),
                    Text('Sayfa $_page / $pageCount'),
                    IconButton(
                      onPressed: _page < pageCount
                          ? () => setState(() => _page++)
                          : null,
                      icon: const Icon(Icons.chevron_right),
                    ),
                  ],
                ),
              if (_error != null)
                Text(
                  'Yenileme başarısız: $_error',
                  style: TextStyle(color: Theme.of(context).colorScheme.error),
                ),
            ],
          ),
        ),
        Positioned(
          right: 20,
          bottom: 16,
          child: FloatingActionButton.extended(
            onPressed: () => _editUser(),
            icon: const Icon(Icons.person_add_alt_1),
            label: const Text('Kullanıcı ekle'),
          ),
        ),
      ],
    );
  }

  String _personnelName(Object? id) {
    final personelId = _intValue(id);
    if (personelId == null) return ' · Personel bağlantısı yok';
    final person = _personnel.where(
      (item) => _intValue(item['personelId']) == personelId,
    );
    return person.isEmpty
        ? ' · Personel #$personelId'
        : ' · ${person.first['adSoyad']}';
  }
}

class _UserForm extends StatefulWidget {
  const _UserForm({this.user, required this.personnel});

  final JsonMap? user;
  final List<JsonMap> personnel;

  @override
  State<_UserForm> createState() => _UserFormState();
}

class _UserFormState extends State<_UserForm> {
  final _formKey = GlobalKey<FormState>();
  late final TextEditingController _username;
  final _password = TextEditingController();
  late String _role;
  int? _personnelId;

  @override
  void initState() {
    super.initState();
    _username = TextEditingController(
      text: widget.user?['kullaniciAdi']?.toString(),
    );
    _role = widget.user?['rol']?.toString() ?? 'Kullanici';
    _personnelId = _intValue(widget.user?['personelId']);
  }

  @override
  void dispose() {
    _username.dispose();
    _password.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final isNew = widget.user == null;
    return AlertDialog(
      title: Text(isNew ? 'Kullanıcı ekle' : 'Kullanıcı düzenle'),
      content: Form(
        key: _formKey,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            TextFormField(
              controller: _username,
              decoration: const InputDecoration(labelText: 'Kullanıcı adı'),
              validator: (value) => value == null || value.trim().length < 3
                  ? 'En az 3 karakter girin.'
                  : null,
            ),
            const SizedBox(height: 10),
            DropdownButtonFormField<int?>(
              initialValue: _personnelId,
              decoration: const InputDecoration(labelText: 'Personel hesabı'),
              items: [
                const DropdownMenuItem<int?>(
                  value: null,
                  child: Text('Personel bağlantısı yok'),
                ),
                ...widget.personnel.map(
                  (person) => DropdownMenuItem<int?>(
                    value: _intValue(person['personelId']),
                    child: Text(person['adSoyad']?.toString() ?? ''),
                  ),
                ),
              ],
              onChanged: (value) => setState(() => _personnelId = value),
            ),
            const SizedBox(height: 10),
            TextFormField(
              controller: _password,
              obscureText: true,
              decoration: InputDecoration(
                labelText: isNew
                    ? 'Parola (en az 12 karakter)'
                    : 'Yeni parola (boş bırakılabilir)',
              ),
              validator: (value) =>
                  isNew && (value == null || value.length < 12)
                  ? 'Parola en az 12 karakter olmalıdır.'
                  : null,
            ),
            if (!isNew) ...[
              const SizedBox(height: 10),
              DropdownButtonFormField<String>(
                initialValue: _role,
                decoration: const InputDecoration(labelText: 'Rol'),
                items: const [
                  DropdownMenuItem(value: 'Kullanici', child: Text('Personel')),
                  DropdownMenuItem(value: 'Admin', child: Text('Yönetici')),
                ],
                onChanged: (value) =>
                    setState(() => _role = value ?? 'Kullanici'),
              ),
            ],
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.pop(context),
          child: const Text('İptal'),
        ),
        FilledButton(
          onPressed: () {
            if (!_formKey.currentState!.validate()) return;
            final data = <String, dynamic>{
              'kullaniciAdi': _username.text.trim(),
              'personelId': _personnelId,
            };
            if (_password.text.isNotEmpty) data['parola'] = _password.text;
            if (!isNew) {
              data['rol'] = _role;
              data['personelIdGuncellensin'] = true;
            }
            Navigator.pop(context, data);
          },
          child: const Text('Kaydet'),
        ),
      ],
    );
  }
}

class _ProfilePage extends StatefulWidget {
  const _ProfilePage({required this.api, required this.user});

  final AuthService api;
  final JsonMap user;

  @override
  State<_ProfilePage> createState() => _ProfilePageState();
}

class _ProfilePageState extends State<_ProfilePage> {
  List<JsonMap> _assignments = [];
  Object? _error;
  bool _loading = true;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final result = await widget.api.request(
        'GET',
        'Demirbas/benim-zimmetlerim',
      );
      if (mounted) setState(() => _assignments = _asMaps(result));
    } catch (error) {
      if (mounted) setState(() => _error = error);
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    if (_loading) {
      return const Center(child: CircularProgressIndicator());
    }
    if (_error != null) {
      return RefreshIndicator(
        onRefresh: _load,
        child: ListView(
          padding: const EdgeInsets.all(24),
          children: [
            const Icon(Icons.person_search_outlined, size: 48),
            const SizedBox(height: 16),
            Text(_error.toString(), textAlign: TextAlign.center),
            if (widget.user['rol'] == 'Admin')
              const Padding(
                padding: EdgeInsets.only(top: 12),
                child: Text(
                  'Kullanıcılar sekmesinden hesabınızı bir personel kaydına bağlayabilirsiniz.',
                  textAlign: TextAlign.center,
                ),
              ),
            const SizedBox(height: 12),
            Center(
              child: OutlinedButton.icon(
                onPressed: _load,
                icon: const Icon(Icons.refresh),
                label: const Text('Yeniden dene'),
              ),
            ),
          ],
        ),
      );
    }
    return RefreshIndicator(
      onRefresh: _load,
      child: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          Card(
            elevation: 0,
            color: Colors.white,
            child: ListTile(
              leading: const CircleAvatar(
                child: Icon(Icons.account_circle_outlined),
              ),
              title: Text(widget.user['kullaniciAdi']?.toString() ?? ''),
              subtitle: Text(
                widget.user['rol'] == 'Admin'
                    ? 'Yönetici hesabı'
                    : 'Personel hesabı',
              ),
            ),
          ),
          const SizedBox(height: 12),
          Text(
            'Üzerimdeki cihazlar (${_assignments.length})',
            style: Theme.of(context).textTheme.titleMedium,
          ),
          if (_assignments.isEmpty)
            const Padding(
              padding: EdgeInsets.symmetric(vertical: 32),
              child: Center(
                child: Text('Üzerinize kayıtlı aktif zimmet bulunmuyor.'),
              ),
            ),
          ..._assignments.map(
            (assignment) => Card(
              elevation: 0,
              color: Colors.white,
              margin: const EdgeInsets.only(top: 8),
              child: ListTile(
                leading: const CircleAvatar(
                  child: Icon(Icons.devices_outlined),
                ),
                title: Text(
                  assignment['demirbasAdi']?.toString() ?? 'Demirbaş',
                ),
                subtitle: Text(
                  '${assignment['departmanAdi'] ?? 'Departman yok'} · '
                  '${assignment['zimmetTarihi'] ?? ''}\n'
                  'Durum: ${_statusLabel(assignment['durum']?.toString() ?? '')}',
                ),
                isThreeLine: true,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _QrScanPage extends StatefulWidget {
  const _QrScanPage({required this.api, required this.isAdmin});

  final AuthService api;
  final bool isAdmin;

  @override
  State<_QrScanPage> createState() => _QrScanPageState();
}

class _QrScanPageState extends State<_QrScanPage> {
  final MobileScannerController _scannerController = MobileScannerController(
    autoZoom: true,
    cameraResolution: const Size(1920, 1080),
    detectionSpeed: DetectionSpeed.noDuplicates,
    lensType: CameraLensType.normal,
  );
  JsonMap? _asset;
  Object? _error;
  String? _lastDetectedCode;
  XFile? _selectedImage;
  bool _lookupInProgress = false;
  bool _working = false;

  Future<void> _scanFromGallery() async {
    if (_lookupInProgress || _working) return;
    setState(() {
      _lookupInProgress = true;
      _error = null;
    });

    try {
      final image = await ImagePicker().pickImage(source: ImageSource.gallery);
      if (image == null || !mounted) return;
      setState(() => _selectedImage = image);

      final capture = await _scannerController.analyzeImage(image.path);
      String? code;
      for (final barcode in capture?.barcodes ?? const <Barcode>[]) {
        final value = barcode.rawValue?.trim();
        if (value != null && value.isNotEmpty) {
          code = value;
          break;
        }
      }

      if (code == null) {
        setState(() {
          _error =
              'Görsel seçildi ancak QR kod çözülemedi. Kod görünür ve net mi? '
              'İstersen kodu elle girerek demirbaşı bulabilirsin.';
        });
        return;
      }

      setState(() {
        _lookupInProgress = false;
        _lastDetectedCode = code;
      });
      await _lookup(code);
    } catch (error) {
      if (mounted) setState(() => _error = error);
    } finally {
      if (mounted) setState(() => _lookupInProgress = false);
    }
  }

  Future<void> _lookup(String code) async {
    if (_lookupInProgress || _working) return;
    setState(() {
      _lookupInProgress = true;
      _error = null;
      _lastDetectedCode = code;
    });
    try {
      final result = await widget.api.request(
        'GET',
        'Demirbas/qr/${Uri.encodeComponent(code)}',
      );
      if (mounted) setState(() => _asset = _asMaps([result]).first);
    } catch (error) {
      if (mounted) setState(() => _error = error);
    } finally {
      if (mounted) setState(() => _lookupInProgress = false);
    }
  }

  Future<void> _enterCodeManually() async {
    final code = await showDialog<String>(
      context: context,
      builder: (context) {
        final controller = TextEditingController();
        return AlertDialog(
          title: const Text('QR / barkod kodunu gir'),
          content: TextField(
            controller: controller,
            autofocus: true,
            textCapitalization: TextCapitalization.characters,
            decoration: const InputDecoration(
              labelText: 'Kod',
              hintText: 'Örn. DMB-000001',
            ),
            onSubmitted: (value) => Navigator.pop(context, value.trim()),
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(context),
              child: const Text('Vazgeç'),
            ),
            FilledButton(
              onPressed: () => Navigator.pop(context, controller.text.trim()),
              child: const Text('Demirbaşı bul'),
            ),
          ],
        );
      },
    );
    if (code == null || code.isEmpty || !mounted) return;
    await _lookup(code);
  }

  Future<void> _reloadAsset() async {
    final code = (_asset?['QrKodu'] ?? _asset?['BarkodKodu'])?.toString();
    if (code == null || code.isEmpty) return;
    try {
      final result = await widget.api.request(
        'GET',
        'Demirbas/qr/${Uri.encodeComponent(code)}',
      );
      if (mounted) setState(() => _asset = _asMaps([result]).first);
    } catch (error) {
      if (mounted) setState(() => _error = error);
    }
  }

  Future<void> _assign() async {
    final asset = _asset;
    if (asset == null) return;
    setState(() => _working = true);
    try {
      final filters = Map<String, dynamic>.from(
        await widget.api.request('GET', 'Demirbas/filtreler') as Map,
      );
      final personnel = _asMaps(filters['personeller']);
      final departments = _asMaps(filters['departmanlar']);
      if (personnel.isEmpty) {
        throw const ApiException(
          'Zimmetleme için önce bir personel kaydı oluşturulmalıdır.',
        );
      }
      if (!mounted) return;
      final form = await showDialog<JsonMap>(
        context: context,
        builder: (context) => _AssignmentForm(
          assets: [asset],
          personnel: personnel,
          departments: departments,
        ),
      );
      if (form == null) return;
      await widget.api.request('POST', 'Demirbas/zimmetle', body: form);
      if (!mounted) return;
      _notify(context, 'Cihaz personele teslim edildi.');
      await _reloadAsset();
    } catch (error) {
      if (mounted) _notify(context, error.toString(), error: true);
    } finally {
      if (mounted) setState(() => _working = false);
    }
  }

  Future<void> _returnAsset() async {
    final id = _intValue(_asset?['DemirbasId']);
    if (id == null) return;
    setState(() => _working = true);
    try {
      await widget.api.request(
        'POST',
        'Demirbas/zimmetten-dusun/$id',
        body: const {},
      );
      if (!mounted) return;
      _notify(context, 'Cihaz geri kabul edildi.');
      await _reloadAsset();
    } catch (error) {
      if (mounted) _notify(context, error.toString(), error: true);
    } finally {
      if (mounted) setState(() => _working = false);
    }
  }

  Future<void> _reportFault() async {
    final id = _intValue(_asset?['DemirbasId']);
    if (id == null) return;
    final description = await showDialog<String>(
      context: context,
      builder: (context) => const _FaultReportDialog(),
    );
    if (description == null) return;
    setState(() => _working = true);
    try {
      await widget.api.request(
        'POST',
        'Demirbas/$id/ariza-bildir',
        body: {'aciklama': description},
      );
      if (!mounted) return;
      _notify(context, 'Arıza bildirimi kaydedildi.');
      await _reloadAsset();
    } catch (error) {
      if (mounted) _notify(context, error.toString(), error: true);
    } finally {
      if (mounted) setState(() => _working = false);
    }
  }

  void _resetScanner() {
    setState(() {
      _asset = null;
      _error = null;
      _lastDetectedCode = null;
      _selectedImage = null;
      _lookupInProgress = false;
    });
  }

  @override
  void dispose() {
    _scannerController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final asset = _asset;
    return Scaffold(
      appBar: AppBar(
        title: const Text('QR / barkod okut'),
        actions: [
          IconButton(
            tooltip: 'Kodu elle gir',
            onPressed: _lookupInProgress || _working
                ? null
                : _enterCodeManually,
            icon: const Icon(Icons.keyboard_alt_outlined),
          ),
          IconButton(
            tooltip: 'Galeriden görsel seç',
            onPressed: _lookupInProgress || _working ? null : _scanFromGallery,
            icon: const Icon(Icons.photo_library_outlined),
          ),
        ],
      ),
      body: Stack(
        children: [
          MobileScanner(
            controller: _scannerController,
            errorBuilder: (context, error) => Center(
              child: Padding(
                padding: const EdgeInsets.all(24),
                child: Text(
                  'Kamera başlatılamadı: ${error.errorCode}',
                  textAlign: TextAlign.center,
                ),
              ),
            ),
            onDetect: (capture) {
              if (_asset != null || _lookupInProgress || _working) return;
              for (final barcode in capture.barcodes) {
                final code = barcode.rawValue?.trim();
                if (code != null && code.isNotEmpty) {
                  setState(() => _lastDetectedCode = code);
                  _lookup(code);
                  break;
                }
              }
            },
          ),
          Positioned(
            right: 12,
            top: 12,
            child: ValueListenableBuilder<MobileScannerState>(
              valueListenable: _scannerController,
              builder: (context, state, child) => IconButton.filledTonal(
                tooltip: state.torchState == TorchState.on
                    ? 'Flaşı kapat'
                    : 'Flaşı aç',
                onPressed: state.torchState != TorchState.unavailable
                    ? _scannerController.toggleTorch
                    : null,
                icon: Icon(
                  state.torchState == TorchState.on
                      ? Icons.flashlight_off
                      : Icons.flashlight_on,
                ),
              ),
            ),
          ),
          if (asset == null && !_lookupInProgress)
            Center(
              child: IgnorePointer(
                child: Container(
                  width: 250,
                  height: 250,
                  decoration: BoxDecoration(
                    border: Border.all(color: Colors.white, width: 3),
                    borderRadius: BorderRadius.circular(24),
                  ),
                ),
              ),
            ),
          if (asset == null && _error == null)
            Positioned(
              left: 24,
              right: 24,
              top: 20,
              child: IgnorePointer(
                child: Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 12,
                    vertical: 8,
                  ),
                  decoration: BoxDecoration(
                    color: Colors.black54,
                    borderRadius: BorderRadius.circular(12),
                  ),
                  child: const Text(
                    'Kodu çerçevenin ortasına alın ve netleyene kadar sabit tutun.',
                    textAlign: TextAlign.center,
                    style: TextStyle(color: Colors.white),
                  ),
                ),
              ),
            ),
          if (_lastDetectedCode != null && asset == null)
            Positioned(
              left: 24,
              right: 24,
              bottom: 24,
              child: Card(
                child: Padding(
                  padding: const EdgeInsets.all(12),
                  child: Text(
                    _lookupInProgress
                        ? 'Kod okundu: $_lastDetectedCode\nDemirbaş aranıyor...'
                        : 'Okunan kod: $_lastDetectedCode',
                    textAlign: TextAlign.center,
                  ),
                ),
              ),
            ),
          if (_lookupInProgress || _working)
            const Center(child: CircularProgressIndicator()),
          if (_error != null && asset == null)
            Positioned(
              left: 16,
              right: 16,
              bottom: _lastDetectedCode == null ? 24 : 112,
              child: Card(
                child: Padding(
                  padding: const EdgeInsets.all(12),
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Text(_error.toString(), textAlign: TextAlign.center),
                      if (_selectedImage != null) ...[
                        const SizedBox(height: 8),
                        ClipRRect(
                          borderRadius: BorderRadius.circular(8),
                          child: Image.file(
                            File(_selectedImage!.path),
                            height: 120,
                            fit: BoxFit.contain,
                            errorBuilder: (context, error, stackTrace) =>
                                const SizedBox.shrink(),
                          ),
                        ),
                      ],
                      TextButton.icon(
                        onPressed: _enterCodeManually,
                        icon: const Icon(Icons.keyboard_alt_outlined),
                        label: const Text('Kodu elle gir'),
                      ),
                      TextButton.icon(
                        onPressed: _resetScanner,
                        icon: const Icon(Icons.qr_code_scanner),
                        label: const Text('Tekrar tara'),
                      ),
                    ],
                  ),
                ),
              ),
            ),
          if (asset != null)
            Positioned.fill(
              child: ColoredBox(
                color: Colors.black54,
                child: SafeArea(
                  child: Center(
                    child: SingleChildScrollView(
                      padding: const EdgeInsets.all(16),
                      child: Card(
                        child: Padding(
                          padding: const EdgeInsets.all(18),
                          child: Column(
                            mainAxisSize: MainAxisSize.min,
                            crossAxisAlignment: CrossAxisAlignment.stretch,
                            children: [
                              Text(
                                asset['DemirbasAdi']?.toString() ?? 'Demirbaş',
                                style: Theme.of(context).textTheme.titleLarge,
                              ),
                              const SizedBox(height: 12),
                              _DetailRow('Kategori', asset['Kategori']),
                              _DetailRow(
                                'Marka / model',
                                '${asset['Marka'] ?? '-'} / ${asset['Model'] ?? '-'}',
                              ),
                              _DetailRow(
                                'Seri numarası',
                                asset['SeriNumarasi'],
                              ),
                              _DetailRow(
                                'Durum',
                                _statusLabel(asset['Durum']?.toString() ?? ''),
                              ),
                              _DetailRow(
                                'Alım tarihi',
                                asset['SatinAlinmaTarihi']
                                    ?.toString()
                                    .split('T')
                                    .first,
                              ),
                              _DetailRow(
                                'Garanti bitişi',
                                asset['GarantiBitisTarihi']
                                    ?.toString()
                                    .split('T')
                                    .first,
                              ),
                              _DetailRow(
                                'Üzerinde olduğu personel',
                                asset['PersonelAdi'],
                              ),
                              _DetailRow('Departman', asset['DepartmanAdi']),
                              if ((asset['QrKodu'] ?? asset['BarkodKodu']) !=
                                  null)
                                Center(
                                  child: QrImageView(
                                    data:
                                        (asset['QrKodu'] ?? asset['BarkodKodu'])
                                            .toString(),
                                    size: 140,
                                    backgroundColor: Colors.white,
                                  ),
                                ),
                              const SizedBox(height: 12),
                              if (widget.isAdmin)
                                FilledButton.icon(
                                  onPressed: _working ? null : _assign,
                                  icon: const Icon(
                                    Icons.assignment_ind_outlined,
                                  ),
                                  label: const Text('Personele teslim et'),
                                ),
                              if (widget.isAdmin &&
                                  asset['Durum']?.toString() == 'Zimmetli')
                                OutlinedButton.icon(
                                  onPressed: _working ? null : _returnAsset,
                                  icon: const Icon(
                                    Icons.assignment_return_outlined,
                                  ),
                                  label: const Text('Cihazı geri kabul et'),
                                ),
                              OutlinedButton.icon(
                                onPressed: _working ? null : _reportFault,
                                icon: const Icon(Icons.build_outlined),
                                label: const Text('Arıza durumu bildir'),
                              ),
                              TextButton.icon(
                                onPressed: _resetScanner,
                                icon: const Icon(Icons.qr_code_scanner),
                                label: const Text('Başka cihaz okut'),
                              ),
                            ],
                          ),
                        ),
                      ),
                    ),
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class _FaultReportDialog extends StatefulWidget {
  const _FaultReportDialog();

  @override
  State<_FaultReportDialog> createState() => _FaultReportDialogState();
}

class _FaultReportDialogState extends State<_FaultReportDialog> {
  final _descriptionController = TextEditingController();

  @override
  void dispose() {
    _descriptionController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
    title: const Text('Arıza bildir'),
    content: TextField(
      controller: _descriptionController,
      autofocus: true,
      maxLines: 4,
      maxLength: 500,
      decoration: const InputDecoration(
        labelText: 'Arıza açıklaması',
        hintText: 'Gözlenen sorunu kısaca açıklayın',
      ),
    ),
    actions: [
      TextButton(
        onPressed: () => Navigator.pop(context),
        child: const Text('İptal'),
      ),
      FilledButton(
        onPressed: () {
          final description = _descriptionController.text.trim();
          if (description.isEmpty) return;
          Navigator.pop(context, description);
        },
        child: const Text('Bildir'),
      ),
    ],
  );
}
