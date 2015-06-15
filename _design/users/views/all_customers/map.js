(function (doc) {
    if (doc.type == 'customer') {
        emit(doc.id, 1);
    }
})